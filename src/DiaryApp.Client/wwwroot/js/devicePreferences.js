const STORAGE_KEY = 'DiaryApp.DevicePreferences';
const LEGACY_COOKIE_NAME = 'DiaryApp.DevicePreferences';

// ============ localStorage (primary) ============

function getFromLocalStorage() {
    try {
        const value = localStorage.getItem(STORAGE_KEY);
        if (!value) {
            return null;
        }
        return JSON.parse(value);
    } catch (error) {
        console.warn('Failed to read from localStorage:', error);
        return null;
    }
}

function saveToLocalStorage(preferences) {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(preferences));
        return true;
    } catch (error) {
        console.warn('Failed to save to localStorage:', error);
        return false;
    }
}

function clearLocalStorage() {
    try {
        localStorage.removeItem(STORAGE_KEY);
        return true;
    } catch (error) {
        console.warn('Failed to clear localStorage:', error);
        return false;
    }
}

// ============ Cookie (legacy fallback) ============

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

function getFromCookie() {
    try {
        const cookieValue = getCookie(LEGACY_COOKIE_NAME);
        if (!cookieValue) {
            return null;
        }
        const decoded = decodeURIComponent(cookieValue);
        return JSON.parse(decoded);
    } catch (error) {
        console.warn('Failed to read from cookie:', error);
        return null;
    }
}

function saveToCookie(preferences) {
    try {
        const json = JSON.stringify(preferences);
        const encoded = encodeURIComponent(json);
        setCookie(LEGACY_COOKIE_NAME, encoded, COOKIE_MAX_AGE);
        return true;
    } catch (error) {
        console.warn('Failed to save to cookie:', error);
        return false;
    }
}

// ============ Public API ============

export function getDevicePreferences() {
    // Try localStorage first
    let preferences = getFromLocalStorage();
    
    // If not in localStorage, try migrating from cookie
    if (!preferences) {
        preferences = getFromCookie();
        if (preferences) {
            // Migrate to localStorage and clear the old cookie
            saveToLocalStorage(preferences);
            deleteCookie(LEGACY_COOKIE_NAME);
            console.info('Migrated device preferences from cookie to localStorage');
        }
    }
    
    if (!preferences) {
        return { cameraDeviceId: null, microphoneDeviceId: null };
    }
    
    return {
        cameraDeviceId: preferences.cameraDeviceId || null,
        microphoneDeviceId: preferences.microphoneDeviceId || null
    };
}

export function setDevicePreferences(cameraDeviceId, microphoneDeviceId) {
    const preferences = {
        cameraDeviceId: cameraDeviceId || null,
        microphoneDeviceId: microphoneDeviceId || null
    };
    
    // Try localStorage first (works on Safari)
    const localStorageSuccess = saveToLocalStorage(preferences);
    
    // Also try cookie as fallback (in case localStorage is disabled)
    const cookieSuccess = saveToCookie(preferences);
    
    // Return true if at least one storage method worked
    return localStorageSuccess || cookieSuccess;
}

export function clearDevicePreferences() {
    const localCleared = clearLocalStorage();
    deleteCookie(LEGACY_COOKIE_NAME);
    return localCleared;
}
