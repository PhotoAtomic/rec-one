let mediaRecorder;
let recordingStream;
let recordedChunks = [];
let recordingMimeType = 'video/webm';
let recorderOptions;

let previewElement = null;
let previewStream = null;
let currentCaptureCleanups = [];
let canvas;
let canvasContext;
let canvasStream;
let canvasAnimationId;
let audioContext;
let audioDestination;
let audioSources = [];

const preferredMimeTypes = [
    'video/webm;codecs=vp9,opus',
    'video/webm;codecs=vp8,opus',
    'video/webm;codecs=vp9',
    'video/webm;codecs=vp8',
    'video/mp4;codecs=h264,aac',
    'video/webm'
];

const vuMeterState = {
    container: null,
    fill: null,
    audioContext: null,
    analyser: null,
    gainNode: null,
    dataArray: null,
    source: null,
    rafId: null
};

function selectMimeType() {
    if (typeof MediaRecorder === 'undefined') {
        return recordingMimeType;
    }

    for (const type of preferredMimeTypes) {
        if (MediaRecorder.isTypeSupported(type)) {
            return type;
        }
    }

    return recordingMimeType;
}

function buildAudioConstraints(options) {
    return options?.microphoneDeviceId && options.microphoneDeviceId !== 'default'
        ? { deviceId: { exact: options.microphoneDeviceId } }
        : true;
}

function buildCameraConstraints(options) {
    const video = options?.cameraDeviceId && options.cameraDeviceId !== 'default'
        ? { deviceId: { exact: options.cameraDeviceId } }
        : true;

    return {
        video,
        audio: buildAudioConstraints(options)
    };
}

async function createCaptureStream(options, captureScreen) {
    if (captureScreen) {
        return createScreenCaptureStream(options);
    }

    const stream = await navigator.mediaDevices.getUserMedia(buildCameraConstraints(options));
    const cleanups = [
        () => stream.getTracks().forEach((track) => track.stop())
    ];
    return { stream, cleanups };
}

async function createScreenCaptureStream(options) {
    const cleanups = [];
    const displayStream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: true });
    cleanups.push(() => displayStream.getTracks().forEach((track) => track.stop()));

    let microphoneStream = null;
    try {
        microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: buildAudioConstraints(options) });
        cleanups.push(() => microphoneStream.getTracks().forEach((track) => track.stop()));
    } catch (error) {
        console.warn('Unable to access microphone during screen capture', error);
    }

    const stream = await composeScreenStream(displayStream, microphoneStream, cleanups);
    return { stream, cleanups };
}

async function composeScreenStream(displayStream, microphoneStream, cleanups) {
    const stream = new MediaStream();
    const videoTrack = displayStream.getVideoTracks()[0];
    if (videoTrack) {
        stream.addTrack(videoTrack);
    }

    const mixedAudioTrack = await createMixedAudioTrack(displayStream, microphoneStream, cleanups);
    if (mixedAudioTrack) {
        stream.addTrack(mixedAudioTrack);
    } else if (microphoneStream) {
        microphoneStream.getAudioTracks().forEach((track) => stream.addTrack(track));
    }

    return stream;
}

async function createMixedAudioTrack(displayStream, microphoneStream, cleanups) {
    const displayAudioTracks = displayStream?.getAudioTracks() ?? [];
    const micAudioTracks = microphoneStream?.getAudioTracks() ?? [];

    if (displayAudioTracks.length === 0 && micAudioTracks.length === 0) {
        return null;
    }

    const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextCtor) {
        const merged = new MediaStream();
        displayAudioTracks.forEach((track) => merged.addTrack(track));
        micAudioTracks.forEach((track) => merged.addTrack(track));
        return merged.getAudioTracks()[0] ?? null;
    }

    const audioContext = new AudioContextCtor();
    cleanups.push(() => audioContext.close());

    const destination = audioContext.createMediaStreamDestination();
    cleanups.push(() => destination.stream.getTracks().forEach((track) => track.stop()));

    const connectTracks = (tracks) => {
        if (!tracks || tracks.length === 0) {
            return;
        }
        const tempStream = new MediaStream();
        tracks.forEach((track) => tempStream.addTrack(track));
        const source = audioContext.createMediaStreamSource(tempStream);
        cleanups.push(() => {
            try {
                source.disconnect();
            } catch (error) {
                console.warn('Failed to disconnect audio source', error);
            }
        });
        source.connect(destination);
    };

    connectTracks(displayAudioTracks);
    connectTracks(micAudioTracks);

    return destination.stream.getAudioTracks()[0] ?? null;
}

async function setupPreview(videoElement, recordingStream) {
    previewElement = videoElement;
    if (previewStream) {
        previewStream.getTracks().forEach((track) => track.stop());
    }

    const clone = recordingStream.clone();
    clone.getAudioTracks().forEach((track) => clone.removeTrack(track));
    previewStream = clone;

    previewElement.defaultMuted = true;
    previewElement.muted = true;
    previewElement.volume = 0;
    previewElement.setAttribute('muted', 'muted');
    previewElement.srcObject = previewStream;
    await previewElement.play().catch((error) => {
        console.warn('Unable to autoplay preview stream', error);
    });
}

function resetPreview() {
    if (previewElement) {
        previewElement.pause();
        previewElement.srcObject = null;
    }
    if (previewStream) {
        previewStream.getTracks().forEach((track) => track.stop());
    }
    previewElement = null;
    previewStream = null;
}

function disposeCleanups(cleanups) {
    if (!cleanups) {
        return;
    }

    cleanups.forEach((cleanup) => {
        try {
            cleanup();
        } catch (error) {
            console.warn('Cleanup failed', error);
        }
    });
}

function createRecorder() {
    const recorder = recorderOptions
        ? new MediaRecorder(recordingStream, recorderOptions)
        : new MediaRecorder(recordingStream);
    recorder.ondataavailable = (event) => {
        if (event.data && event.data.size > 0) {
            recordedChunks.push(event.data);
        }
    };
    recorder.onerror = (error) => console.error('Recorder error', error);
    return recorder;
}

function configureCanvasForStream(sourceStream) {
    const track = sourceStream.getVideoTracks()[0];
    const settings = track?.getSettings?.() ?? {};
    const width = settings.width ?? canvas?.width ?? 1280;
    const height = settings.height ?? canvas?.height ?? 720;

    if (!canvas) {
        canvas = document.createElement('canvas');
    }

    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
    }

    canvasContext = canvas.getContext('2d');
    if (!canvasStream) {
        canvasStream = canvas.captureStream(30);
    }
}

function startRenderingLoop() {
    if (canvasAnimationId) {
        return;
    }

    const render = () => {
        if (canvasContext && previewElement && previewElement.readyState >= 2) {
            canvasContext.drawImage(previewElement, 0, 0, canvas.width, canvas.height);
        }
        canvasAnimationId = requestAnimationFrame(render);
    };

    render();
}

function stopRenderingLoop() {
    if (canvasAnimationId) {
        cancelAnimationFrame(canvasAnimationId);
        canvasAnimationId = null;
    }
}

function ensureRecordingStream() {
    if (!recordingStream) {
        recordingStream = new MediaStream();
    }

    if (canvasStream) {
        const canvasTrack = canvasStream.getVideoTracks()[0];
        if (canvasTrack && recordingStream.getVideoTracks().length === 0) {
            recordingStream.addTrack(canvasTrack);
        }
    }
}

function ensureAudioGraph() {
    if (!audioContext) {
        const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextCtor) {
            return false;
        }
        audioContext = new AudioContextCtor();
    }

    if (!audioDestination) {
        audioDestination = audioContext.createMediaStreamDestination();
        const destinationTrack = audioDestination.stream.getAudioTracks()[0];
        if (destinationTrack) {
            recordingStream.addTrack(destinationTrack);
        }
    }

    return true;
}

function updateAudioTracks(sourceStream) {
    if (!recordingStream) {
        recordingStream = new MediaStream();
    }

    if (!ensureAudioGraph()) {
        // Fallback: if no audio context, copy tracks directly without stopping old ones
        sourceStream.getAudioTracks().forEach((track) => recordingStream.addTrack(track.clone()));
        return;
    }

    audioSources.forEach((source) => {
        try {
            source.disconnect();
        } catch (error) {
            console.warn('Failed to disconnect audio source', error);
        }
    });
    audioSources = [];

    sourceStream.getAudioTracks().forEach((track) => {
        const tempStream = new MediaStream([track]);
        const source = audioContext.createMediaStreamSource(tempStream);
        source.connect(audioDestination);
        audioSources.push(source);
    });
}

function startVuMeter(stream, meterElement) {
    stopVuMeter();
    if (!meterElement || typeof meterElement.querySelector !== 'function') {
        return;
    }

    const fill = meterElement.querySelector('[data-role="vu-fill"]');
    const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
    if (!fill || !AudioContextCtor) {
        return;
    }

    const audioContext = new AudioContextCtor();
    const resumePromise = audioContext.state === 'suspended'
        ? audioContext.resume().catch((error) => console.warn('Unable to resume audio context', error))
        : Promise.resolve();
    const source = audioContext.createMediaStreamSource(stream);
    const analyser = audioContext.createAnalyser();
    analyser.fftSize = 512;
    const gainNode = audioContext.createGain();
    gainNode.gain.value = 0;
    source.connect(analyser);
    analyser.connect(gainNode);
    gainNode.connect(audioContext.destination);
    const dataArray = new Uint8Array(analyser.frequencyBinCount);

    vuMeterState.container = meterElement;
    vuMeterState.fill = fill;
    vuMeterState.audioContext = audioContext;
    vuMeterState.analyser = analyser;
    vuMeterState.gainNode = gainNode;
    vuMeterState.dataArray = dataArray;
    vuMeterState.source = source;

    const update = () => {
        if (!vuMeterState.analyser || !vuMeterState.fill) {
            return;
        }

        vuMeterState.analyser.getByteTimeDomainData(vuMeterState.dataArray);
        let sum = 0;
        for (let i = 0; i < vuMeterState.dataArray.length; i += 1) {
            const value = (vuMeterState.dataArray[i] - 128) / 128;
            sum += value * value;
        }
        const rms = Math.sqrt(sum / vuMeterState.dataArray.length);
        const level = Math.min(1, rms * 4);
        vuMeterState.fill.style.width = `${(level * 100).toFixed(1)}%`;
        vuMeterState.rafId = requestAnimationFrame(update);
    };

    resumePromise.finally(update);
}

function stopVuMeter() {
    if (vuMeterState.rafId) {
        cancelAnimationFrame(vuMeterState.rafId);
    }
    if (vuMeterState.source) {
        vuMeterState.source.disconnect();
    }
    if (vuMeterState.analyser) {
        vuMeterState.analyser.disconnect();
    }
    if (vuMeterState.gainNode) {
        vuMeterState.gainNode.disconnect();
    }
    if (vuMeterState.audioContext) {
        try {
            vuMeterState.audioContext.close();
        } catch (error) {
            console.warn('Error while closing audio context', error);
        }
    }
    if (vuMeterState.fill) {
        vuMeterState.fill.style.width = '0%';
    }

    vuMeterState.container = null;
    vuMeterState.fill = null;
    vuMeterState.audioContext = null;
    vuMeterState.analyser = null;
    vuMeterState.gainNode = null;
    vuMeterState.dataArray = null;
    vuMeterState.source = null;
    vuMeterState.rafId = null;
}

export async function startRecording(videoElement, options, meterElement, captureScreen = false) {
    const capture = await createCaptureStream(options, captureScreen);
    currentCaptureCleanups = capture.cleanups;

    await setupPreview(videoElement, capture.stream);
    configureCanvasForStream(capture.stream);
    startRenderingLoop();
    recordingStream = new MediaStream();
    ensureRecordingStream();
    updateAudioTracks(capture.stream);

    recordedChunks = [];
    recordingMimeType = selectMimeType();
    recorderOptions = recordingMimeType ? { mimeType: recordingMimeType } : undefined;
    mediaRecorder = createRecorder();
    startVuMeter(recordingStream, meterElement);
    mediaRecorder.start();
}

export async function switchSource(videoElement, options, meterElement, captureScreen = false) {
    if (!mediaRecorder || mediaRecorder.state !== 'recording' || !recordingStream) {
        return;
    }

    const capture = await createCaptureStream(options, captureScreen);
    await setupPreview(videoElement, capture.stream);
    configureCanvasForStream(capture.stream);
    startRenderingLoop();
    updateAudioTracks(capture.stream);
    ensureRecordingStream();
    stopVuMeter();
    startVuMeter(recordingStream, meterElement);
    disposeCleanups(currentCaptureCleanups);
    currentCaptureCleanups = capture.cleanups;
}

export async function stopRecording() {
    if (mediaRecorder && mediaRecorder.state !== 'inactive') {
        await new Promise((resolve) => {
            mediaRecorder.addEventListener('stop', resolve, { once: true });
            mediaRecorder.stop();
        });
    }

    if (mediaRecorder?.stream) {
        mediaRecorder.stream.getTracks().forEach((track) => track.stop());
    }

    stopVuMeter();
    resetPreview();
    disposeCleanups(currentCaptureCleanups);
    currentCaptureCleanups = [];
    stopRenderingLoop();
    if (canvasStream) {
        canvasStream.getTracks().forEach((track) => track.stop());
        canvasStream = null;
    }
    canvas = null;
    canvasContext = null;
    audioSources.forEach((source) => {
        try {
            source.disconnect();
        } catch (error) {
            console.warn('Failed to disconnect audio source', error);
        }
    });
    audioSources = [];
    if (audioDestination) {
        audioDestination.stream.getTracks().forEach((track) => track.stop());
        audioDestination = null;
    }
    if (audioContext) {
        await audioContext.close();
        audioContext = null;
    }
    recordingStream = null;
}

export async function getRecording() {
    if (recordedChunks.length === 0) {
        return new Uint8Array();
    }

    const blob = new Blob(recordedChunks, { type: recordingMimeType || 'video/webm' });
    const buffer = await blob.arrayBuffer();
    recordedChunks = [];
    return new Uint8Array(buffer);
}

export function supportsScreenCapture() {
    return typeof navigator?.mediaDevices?.getDisplayMedia === 'function';
}
