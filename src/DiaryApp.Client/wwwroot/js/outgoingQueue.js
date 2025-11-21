const dbName = 'diaryapp-outgoing';
const storeName = 'outgoingEntries';
const dbVersion = 1;

function openDb() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(dbName, dbVersion);
        request.onupgradeneeded = () => {
            const db = request.result;
            if (!db.objectStoreNames.contains(storeName)) {
                db.createObjectStore(storeName, { keyPath: 'id' });
            }
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function runTransaction(db, mode, fn) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(storeName, mode);
        const store = tx.objectStore(storeName);
        fn(store, tx);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
    });
}

export async function enqueue(entry) {
    const db = await openDb();
    await runTransaction(db, 'readwrite', (store) => {
        const payload = {
            ...entry,
            createdAt: entry.createdAt || new Date().toISOString(),
            sizeBytes: entry.sizeBytes ?? entry.data?.byteLength ?? 0,
            data: entry.data instanceof Uint8Array ? entry.data.buffer : entry.data
        };
        store.put(payload);
    });
    db.close();
}

export async function list() {
    const db = await openDb();
    const records = [];
    await runTransaction(db, 'readonly', (store) => {
        const request = store.openCursor();
        request.onsuccess = (event) => {
            const cursor = event.target.result;
            if (cursor) {
                const value = cursor.value;
                records.push({
                    id: value.id,
                    title: value.title || 'Untitled entry',
                    description: value.description || '',
                    tags: value.tags || '',
                    fileName: value.fileName || 'entry.webm',
                    createdAt: value.createdAt || new Date().toISOString(),
                    sizeBytes: value.sizeBytes ?? value.data?.byteLength ?? 0
                });
                cursor.continue();
            }
        };
    });
    db.close();
    return records;
}

export async function get(id) {
    const db = await openDb();
    let value = null;
    await runTransaction(db, 'readonly', (store) => {
        const request = store.get(id);
        request.onsuccess = () => {
            if (request.result) {
                const result = request.result;
                value = {
                    id: result.id,
                    title: result.title || 'Untitled entry',
                    description: result.description || '',
                    tags: result.tags || '',
                    fileName: result.fileName || 'entry.webm',
                    createdAt: result.createdAt || new Date().toISOString(),
                    sizeBytes: result.sizeBytes ?? result.data?.byteLength ?? 0,
                    data: result.data ? new Uint8Array(result.data) : null
                };
            }
        };
    });
    db.close();
    return value;
}

export async function remove(id) {
    const db = await openDb();
    await runTransaction(db, 'readwrite', (store) => store.delete(id));
    db.close();
}
