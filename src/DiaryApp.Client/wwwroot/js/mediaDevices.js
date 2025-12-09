const MEDIA_KINDS = new Set(['videoinput', 'audioinput']);

export async function listDevices(requestPermissions = false) {
    if (!navigator.mediaDevices?.enumerateDevices) {
        return [];
    }

    if (requestPermissions && canPromptForPermissions()) {
        const permissionResult = await tryPromptForPermissions();
        if (!permissionResult.success) {
            return [];
        }
    }

    let devices;
    try {
        devices = await navigator.mediaDevices.enumerateDevices();
    } catch (error) {
        console.warn('Unable to enumerate media devices:', error);
        return [];
    }

    const hasLabels = hasUsableLabels(devices);

    const labeledDevices = devices
        .filter((device) => MEDIA_KINDS.has(device.kind))
        .map((device) => ({
            deviceId: device.deviceId || 'default',
            kind: device.kind,
            label: device.label?.trim() ?? ''
        }))
        .filter((device) => device.label !== '');

    return hasLabels ? labeledDevices : [];
}

export function getBrowserLanguage() {
    // Get the browser's preferred language
    // navigator.language returns a BCP 47 language tag like "en-US", "it-IT", etc.
    return navigator.language || navigator.userLanguage || null;
}

// Check if permissions have been granted (useful to know before prompting)
export async function hasMediaPermissions() {
    if (!navigator.mediaDevices?.enumerateDevices) {
        return false;
    }

    const devices = await navigator.mediaDevices.enumerateDevices();
    return devices.some(device => 
        MEDIA_KINDS.has(device.kind) && 
        device.label && 
        device.label.trim() !== ''
    );
}

// Explicitly request media permissions - must be called from user gesture (click/tap)
export async function requestMediaPermissions() {
    if (!canPromptForPermissions()) {
        return { success: false, error: 'getUserMedia not supported' };
    }

    const result = await tryPromptForPermissions();
    if (result.success) {
        return { success: true, error: null, audioOnly: result.audioOnly };
    }

    return { success: false, error: result.error || 'Permission denied' };
}

function hasUsableLabels(devices) {
    return devices.some(device =>
        MEDIA_KINDS.has(device.kind) &&
        device.label &&
        device.label.trim() !== ''
    );
}

function canPromptForPermissions() {
    return !!(navigator.mediaDevices?.getUserMedia || legacyGetUserMedia());
}

function legacyGetUserMedia() {
    return navigator.getUserMedia || navigator.webkitGetUserMedia || navigator.mozGetUserMedia || navigator.msGetUserMedia;
}

async function tryPromptForPermissions() {
    try {
        await warmUpPermissions(true);
        return { success: true, audioOnly: false };
    } catch (videoError) {
        try {
            await warmUpPermissions(false);
            return { success: true, audioOnly: true };
        } catch (audioError) {
            return { success: false, error: formatMediaError(audioError) };
        }
    }
}

async function warmUpPermissions(includeVideo) {
    const constraints = includeVideo
        ? { audio: true, video: { facingMode: 'user' } }
        : { audio: true };

    const stream = await requestStream(constraints);
    stopStream(stream);
}

async function requestStream(constraints) {
    if (navigator.mediaDevices?.getUserMedia) {
        return navigator.mediaDevices.getUserMedia(constraints);
    }

    const legacy = legacyGetUserMedia();
    if (!legacy) {
        throw new Error('getUserMedia not supported');
    }

    return new Promise((resolve, reject) => {
        legacy.call(navigator, constraints, resolve, reject);
    });
}

function stopStream(stream) {
    if (!stream?.getTracks) {
        return;
    }

    stream.getTracks().forEach((track) => {
        try {
            track.stop();
        } catch (error) {
            // Ignore track stop failures
        }
    });
}

function formatMediaError(error) {
    if (!error) {
        return 'Permission denied';
    }

    if (!window.isSecureContext && (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError')) {
        return 'Camera/microphone access requires HTTPS on this device.';
    }

    switch (error.name) {
        case 'NotAllowedError':
        case 'PermissionDeniedError':
            return 'Permission denied by browser';
        case 'NotReadableError':
            return 'Camera or microphone is already in use';
        case 'NotFoundError':
        case 'DevicesNotFoundError':
            return 'No camera or microphone detected';
        default:
            return error.message || 'Permission denied';
    }
}
