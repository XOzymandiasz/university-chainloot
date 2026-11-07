export type ClusterAction = 'issue' | 'transfer' | 'redeem'

export interface SigningIdentity {
  address: string
  publicKey: string
  privateKey: CryptoKey
}

export interface SignedOperation {
  operationId: string
  type: ClusterAction
  assetId: string
  sender: string
  recipient: string
  nonce: number
  publicKey: string
  signature: string
}

export async function signOperation(
  user: SigningIdentity,
  type: ClusterAction,
  assetId: string,
  recipient: string,
  nonce: number,
): Promise<SignedOperation> {
  const operation: SignedOperation = {
    operationId: crypto.randomUUID(),
    type,
    assetId,
    sender: user.address,
    recipient,
    nonce,
    publicKey: user.publicKey,
    signature: '',
  }
  const text = [
    'chainloot:v1',
    operation.operationId,
    operation.type,
    operation.assetId,
    operation.sender,
    operation.recipient,
    String(operation.nonce),
  ].join(String.fromCharCode(10))
  const signature = new Uint8Array(
    await crypto.subtle.sign(
      { name: 'ECDSA', hash: 'SHA-256' },
      user.privateKey,
      new TextEncoder().encode(text),
    ),
  )
  operation.signature = btoa(String.fromCharCode(...signature))
  return operation
}
