export async function checkMediaPermissions() {
    try {
        if (!navigator.mediaDevices?.getUserMedia) {
            return { hasAccess: false, needsPermission: true };
        }

        // Check permission state if available
        if (navigator.permissions && navigator.permissions.query) {
            try {
                const cameraPermission = await navigator.permissions.query({ name: 'camera' });
                const microphonePermission = await navigator.permissions.query({ name: 'microphone' });
                
                const cameraGranted = cameraPermission.state === 'granted';
                const micGranted = microphonePermission.state === 'granted';
                const needsPermission = cameraPermission.state === 'prompt' || microphonePermission.state === 'prompt';
                
                return {
                    hasAccess: cameraGranted && micGranted,
                    needsPermission: needsPermission
                };
            } catch (error) {
                // Permissions API not supported for media, fallback to enumeration check
                console.warn('Permissions API not available for media devices:', error);
            }
        }

        // Fallback: check if we can enumerate devices with labels
        const devices = await navigator.mediaDevices.enumerateDevices();
        const hasLabels = devices.some(device => device.label && device.label.trim() !== '');
        
        return {
            hasAccess: hasLabels,
            needsPermission: !hasLabels
        };
    } catch (error) {
        console.error('Error checking media permissions:', error);
        return { hasAccess: false, needsPermission: true };
    }
}
