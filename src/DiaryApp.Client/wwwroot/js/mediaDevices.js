export async function listDevices(requestPermissions = false) {
    if (!navigator.mediaDevices?.enumerateDevices) {
        return [];
    }

    if (requestPermissions && navigator.mediaDevices.getUserMedia) {
        try {
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: true });
            stream.getTracks().forEach((track) => track.stop());
        } catch (error) {
            console.warn('Unable to pre-authorize media devices:', error);
        }
    }

    const devices = await navigator.mediaDevices.enumerateDevices();
    return devices
        .filter((device) => device.kind === 'videoinput' || device.kind === 'audioinput')
        .map((device) => ({
            deviceId: device.deviceId || 'default',
            kind: device.kind,
            label: device.label || device.kind
        }));
}

export function getBrowserLanguage() {
    // Get the browser's preferred language
    // navigator.language returns a BCP 47 language tag like "en-US", "it-IT", etc.
    return navigator.language || navigator.userLanguage || null;
}
