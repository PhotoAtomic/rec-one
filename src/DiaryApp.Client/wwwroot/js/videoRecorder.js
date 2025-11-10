let mediaRecorder;
let recordedChunks = [];
let recordingMimeType = 'video/webm';

const preferredMimeTypes = [
    'video/webm;codecs=vp9,opus',
    'video/webm;codecs=vp8,opus',
    'video/webm;codecs=vp9',
    'video/webm;codecs=vp8',
    'video/mp4;codecs=h264,aac',
    'video/webm'
];

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

    videoElement.srcObject = stream;
    videoElement.muted = true;
    await videoElement.play();
    return stream;
}

export async function startRecording(videoElement, options) {
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
