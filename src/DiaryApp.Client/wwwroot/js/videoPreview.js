async function waitForEvent(target, eventName) {
    return new Promise((resolve, reject) => {
        const onResolve = () => {
            cleanup();
            resolve();
        };

        const onError = () => {
            cleanup();
            reject(new Error(`Failed while waiting for ${eventName}`));
        };

        const cleanup = () => {
            target.removeEventListener(eventName, onResolve);
            target.removeEventListener('error', onError);
        };

        target.addEventListener(eventName, onResolve, { once: true });
        target.addEventListener('error', onError, { once: true });
    });
}

export async function generatePreview(imageElement, videoUrl, second = 5) {
    if (!imageElement || !videoUrl) {
        return;
    }

    const video = document.createElement('video');
    video.crossOrigin = 'anonymous';
    video.preload = 'auto';
    video.muted = true;
    video.playsInline = true;
    video.src = videoUrl;

    try {
        await waitForEvent(video, 'loadeddata');
        const targetSecond = Math.max(0, Math.min(second, (video.duration || second) - 0.1));
        if (!Number.isFinite(targetSecond) || targetSecond < 0) {
            throw new Error('Invalid preview second.');
        }

        if (video.readyState < 2) {
            await waitForEvent(video, 'loadedmetadata');
        }

        video.currentTime = targetSecond;
        await waitForEvent(video, 'seeked');

        const width = video.videoWidth || 640;
        const height = video.videoHeight || 360;
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const context = canvas.getContext('2d');
        context.drawImage(video, 0, 0, width, height);
        const dataUrl = canvas.toDataURL('image/jpeg', 0.8);
        imageElement.src = dataUrl;
    } catch (error) {
        console.warn('Failed to create video preview', error);
    } finally {
        video.removeAttribute('src');
        video.load();
    }
}
