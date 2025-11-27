const COOKIE_NAME = 'DiaryApp.DevicePreferences';
const COOKIE_MAX_AGE = 365 * 24 * 60 * 60; // 1 year in seconds

function getCookie(name) {
    const value = `; ${document.cookie}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) {
        return parts.pop().split(';').shift();
    }
    return null;
}

function setCookie(name, value, maxAge) {
    const secure = window.location.protocol === 'https:' ? '; Secure' : '';
    document.cookie = `${name}=${value}; path=/; max-age=${maxAge}; SameSite=Lax${secure}`;
}

function deleteCookie(name) {
    document.cookie = `${name}=; path=/; max-age=0`;
}

export function getDevicePreferences() {
    try {
        const cookieValue = getCookie(COOKIE_NAME);
        if (!cookieValue) {
            return { cameraDeviceId: null, microphoneDeviceId: null };
        }

        const decoded = decodeURIComponent(cookieValue);
        const preferences = JSON.parse(decoded);
        
        return {
            cameraDeviceId: preferences.cameraDeviceId || null,
            microphoneDeviceId: preferences.microphoneDeviceId || null
        };
    } catch (error) {
        console.warn('Failed to read device preferences from cookie:', error);
        return { cameraDeviceId: null, microphoneDeviceId: null };
    }
}

export function setDevicePreferences(cameraDeviceId, microphoneDeviceId) {
    try {
        const preferences = {
            cameraDeviceId: cameraDeviceId || null,
            microphoneDeviceId: microphoneDeviceId || null
        };
        
        const json = JSON.stringify(preferences);
        const encoded = encodeURIComponent(json);
        setCookie(COOKIE_NAME, encoded, COOKIE_MAX_AGE);
        return true;
    } catch (error) {
        console.error('Failed to save device preferences to cookie:', error);
        return false;
    }
}

export function clearDevicePreferences() {
    deleteCookie(COOKIE_NAME);
}
