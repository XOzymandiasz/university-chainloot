export interface LocalIdentity {
  name: string
  address: string
  publicKey: string
  privateKey: CryptoKey
}

const databaseName = 'chainloot-local-identities-v1'

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(databaseName, 1)
    request.onupgradeneeded = () =>
      request.result.createObjectStore('identities', { keyPath: 'name' })
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error)
  })
}

export async function listIdentities(): Promise<LocalIdentity[]> {
  const db = await openDatabase()
  try {
    return await new Promise((resolve, reject) => {
      const request = db
        .transaction('identities', 'readonly')
        .objectStore('identities')
        .getAll()
      request.onsuccess = () => resolve(request.result as LocalIdentity[])
      request.onerror = () => reject(request.error)
    })
  } finally {
    db.close()
  }
}

export async function generateIdentityKeys(
  name: string,
): Promise<LocalIdentity> {
  const cleanName = name.trim()
  if (!cleanName || cleanName.length > 40)
    throw new Error('Wpisz nazwę użytkownika (maksymalnie 40 znaków).')
  const keys = await crypto.subtle.generateKey(
    { name: 'ECDSA', namedCurve: 'P-256' },
    false,
    ['sign', 'verify'],
  )
  const publicBytes = new Uint8Array(
    await crypto.subtle.exportKey('spki', keys.publicKey),
  )
  const digest = new Uint8Array(
    await crypto.subtle.digest('SHA-256', publicBytes),
  )
  const address =
    '0x' +
    [...digest.slice(-20)]
      .map((byte) => byte.toString(16).padStart(2, '0'))
      .join('')
  const publicKey = btoa(String.fromCharCode(...publicBytes))
  return { name: cleanName, address, publicKey, privateKey: keys.privateKey }
}

export async function createIdentity(name: string): Promise<LocalIdentity> {
  const identity = await generateIdentityKeys(name)
  const db = await openDatabase()
  try {
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction('identities', 'readwrite')
      transaction.objectStore('identities').add(identity)
      transaction.oncomplete = () => resolve()
      transaction.onerror = () => reject(transaction.error)
      transaction.onabort = () => reject(transaction.error)
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'ConstraintError') {
      throw new Error(
        'Użytkownik o tej nazwie już istnieje w tej przeglądarce.',
      )
    }
    throw error
  } finally {
    db.close()
  }
  return identity
}
