let mediaRecorder;
let recordedChunks = [];

async function ensureStream(videoElement) {
    if (!videoElement) {
        throw new Error('Video element missing');
    }

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        throw new Error('Media devices are not supported in this browser.');
    }

    const stream = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
    videoElement.srcObject = stream;
    await videoElement.play();
    return stream;
}

export async function startRecording(videoElement) {
    const stream = await ensureStream(videoElement);
    recordedChunks = [];
    mediaRecorder = new MediaRecorder(stream);
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

    const blob = new Blob(recordedChunks, { type: 'video/webm' });
    const buffer = await blob.arrayBuffer();
    recordedChunks = [];
    return new Uint8Array(buffer);
}
