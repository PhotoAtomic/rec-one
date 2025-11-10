let mediaRecorder;
let recordedChunks = [];
let recordingMimeType = 'video/webm';
let previewElement = null;
let previewStream = null;

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

function buildConstraints(options) {
    const parsedCamera = options?.cameraDeviceId && options.cameraDeviceId !== 'default'
        ? { deviceId: { exact: options.cameraDeviceId } }
        : true;
    const parsedMicrophone = options?.microphoneDeviceId && options.microphoneDeviceId !== 'default'
        ? { deviceId: { exact: options.microphoneDeviceId } }
        : true;

    return {
        video: parsedCamera,
        audio: parsedMicrophone
    };
}

async function ensureStream(videoElement, options) {
    if (!videoElement) {
        throw new Error('Video element missing');
    }

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        throw new Error('Media devices are not supported in this browser.');
    }

    const constraints = buildConstraints(options);
    let stream;
    try {
        stream = await navigator.mediaDevices.getUserMedia(constraints);
    } catch (error) {
        console.warn('Preferred media devices unavailable, falling back to defaults.', error);
        stream = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
    }

    previewElement = videoElement;
    previewStream = stream.clone();
    previewStream.getAudioTracks().forEach((track) => {
        previewStream.removeTrack(track);
        try {
            track.stop();
        } catch (error) {
            console.warn('Unable to stop preview audio track', error);
        }
    });
    previewElement.defaultMuted = true;
    previewElement.muted = true;
    previewElement.volume = 0;
    previewElement.setAttribute('muted', 'muted');
    previewElement.pause();
    previewElement.srcObject = previewStream;
    await previewElement.play().catch((error) => {
        console.warn('Unable to autoplay preview stream', error);
    });
    return stream;
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

export async function startRecording(videoElement, options, meterElement) {
    const stream = await ensureStream(videoElement, options);
    recordedChunks = [];
    recordingMimeType = selectMimeType();
    const recorderOptions = recordingMimeType ? { mimeType: recordingMimeType } : undefined;
    try {
        mediaRecorder = new MediaRecorder(stream, recorderOptions);
    } catch (error) {
        console.warn('Falling back to default MediaRecorder configuration.', error);
        mediaRecorder = new MediaRecorder(stream);
    }
    mediaRecorder.ondataavailable = (event) => {
        if (event.data && event.data.size > 0) {
            recordedChunks.push(event.data);
        }
    };
    startVuMeter(stream, meterElement);
    mediaRecorder.start();
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

    if (previewElement) {
        previewElement.pause();
        previewElement.srcObject = null;
    }
    if (previewStream) {
        previewStream.getTracks().forEach((track) => track.stop());
    }
    previewElement = null;
    previewStream = null;

    stopVuMeter();
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
