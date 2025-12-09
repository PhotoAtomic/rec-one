export async function listDevices(requestPermissions = false) {
    if (!navigator.mediaDevices?.enumerateDevices) {
        return [];
    }

    let devices = await navigator.mediaDevices.enumerateDevices();
    let hasLabels = hasUsableLabels(devices);

    // If we don't have labels and requestPermissions is true, request access
    // This is especially important for Safari which requires explicit user gesture
    if (requestPermissions && !hasLabels && navigator.mediaDevices.getUserMedia) {
        try {
            // Request both audio and video permissions
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: true });
            // Stop all tracks immediately - we just needed the permission prompt
            stream.getTracks().forEach((track) => track.stop());
            // Re-enumerate devices now that we have permission
            devices = await navigator.mediaDevices.enumerateDevices();
            hasLabels = hasUsableLabels(devices);
        } catch (error) {
            // Try requesting just audio if video fails (e.g., no camera)
            try {
                const audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
                audioStream.getTracks().forEach((track) => track.stop());
                devices = await navigator.mediaDevices.enumerateDevices();
                hasLabels = hasUsableLabels(devices);
            } catch (audioError) {
                console.warn('Unable to get media permissions:', audioError);
            }
        }
    }

    const labeledDevices = devices
        .filter((device) => device.kind === 'videoinput' || device.kind === 'audioinput')
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
        (device.kind === 'videoinput' || device.kind === 'audioinput') && 
        device.label && 
        device.label.trim() !== ''
    );
}

// Explicitly request media permissions - must be called from user gesture (click/tap)
export async function requestMediaPermissions() {
    if (!navigator.mediaDevices?.getUserMedia) {
        return { success: false, error: 'getUserMedia not supported' };
    }

    try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: true });
        stream.getTracks().forEach((track) => track.stop());
        return { success: true, error: null };
    } catch (error) {
        // Try audio only if video fails
        try {
            const audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
            audioStream.getTracks().forEach((track) => track.stop());
            return { success: true, error: null, audioOnly: true };
        } catch (audioError) {
            return { success: false, error: audioError.message || 'Permission denied' };
        }
    }
}

function hasUsableLabels(devices) {
    return devices.some(device =>
        (device.kind === 'videoinput' || device.kind === 'audioinput') &&
        device.label &&
        device.label.trim() !== ''
    );
}
