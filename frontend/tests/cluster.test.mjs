import assert from 'node:assert/strict'
import { randomBytes } from 'node:crypto'
import { generateIdentityKeys } from '../src/cluster-identity.ts'
import { signOperation } from '../src/cluster-signing.ts'
import { execFile as execFileCallback } from 'node:child_process'
import { createServer } from 'node:net'
import { promisify } from 'node:util'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const execFile = promisify(execFileCallback)
const project = 'chainloot-test-' + randomBytes(4).toString('hex')
const root = fileURLToPath(new URL('../../', import.meta.url))
const composeFile = fileURLToPath(
  new URL('../../docker-compose.cluster.yml', import.meta.url),
)
const docker = process.env.CHAINLOOT_DOCKER || 'docker'

async function freePort() {
  const server = createServer()
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  const port = server.address().port
  await new Promise((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  )
  return port
}
async function compose(env, ...args) {
  return execFile(
    docker,
    ['compose', '-p', project, '-f', composeFile, ...args],
    {
      cwd: root,
      env,
      timeout: 120000,
      maxBuffer: 1024 * 1024,
    },
  )
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
async function waitFor(predicate, message, timeout = 20000) {
  const until = Date.now() + timeout
  while (Date.now() < until) {
    try {
      const result = await predicate()
      if (result) return result
    } catch {
      // The local service may be restarting.
    }
    await sleep(200)
  }
  throw new Error(message)
}
async function get(base, path) {
  const response = await fetch(base + path, {
    signal: AbortSignal.timeout(2000),
  })
  if (!response.ok) throw new Error('HTTP ' + response.status)
  return response.json()
}
async function post(base, path, data, timeout = 10000) {
  const response = await fetch(base + path, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(data),
    signal: AbortSignal.timeout(timeout),
  })
  const text = await response.text()
  return { http: response.status, body: text ? JSON.parse(text) : null }
}
async function identity() {
  const user = await generateIdentityKeys(
    'test-' + randomBytes(4).toString('hex'),
  )
  assert.equal(user.privateKey.extractable, false)
  await assert.rejects(() => crypto.subtle.exportKey('pkcs8', user.privateKey))
  return user
}
async function operation(user, type, assetId, recipient, nonce) {
  return signOperation(user, type, assetId, recipient, nonce)
}
async function assetEverywhere(bases, id, owner, status = 'ACTIVE') {
  return waitFor(async () => {
    const states = await Promise.all(
      bases.map((base) => get(base, '/cluster/assets/' + id)),
    )
    return states.every(
      (item) => item.owner === owner && item.status === status,
    )
  }, 'Węzły nie uzgodniły stanu ' + id)
}
async function readyLeader(bases) {
  return waitFor(async () => {
    const states = await Promise.all(
      bases.map((base) => get(base, '/cluster/status').catch(() => null)),
    )
    return states.find((state) => state?.role === 'leader' && state.ready)
  }, 'Brak gotowego lidera')
}

test('T1–T14: lokalny system rozproszony', { timeout: 180000 }, async () => {
  const ports = await Promise.all([
    freePort(),
    freePort(),
    freePort(),
    freePort(),
  ])
  const bases = ports.slice(0, 3).map((port) => 'http://127.0.0.1:' + port)
  const uiBase = 'http://127.0.0.1:' + ports[3]
  const env = {
    ...process.env,
    CLUSTER_PORT_A: String(ports[0]),
    CLUSTER_PORT_B: String(ports[1]),
    CLUSTER_PORT_C: String(ports[2]),
    CLUSTER_UI_PORT: String(ports[3]),
  }
  let created = false
  try {
    const existing = await compose(env, 'ps', '--all', '--quiet')
    assert.equal(
      existing.stdout.trim(),
      '',
      'Kolizja identyfikatora środowiska testowego.',
    )
    created = true
    await compose(env, 'up', '-d', '--no-build', '--pull', 'never')
    await readyLeader(bases)
    const ui = await fetch(uiBase + '/cluster.html')
    assert.equal(ui.status, 200)
    const html = await ui.text()
    const styles = [...html.matchAll(/href="([^"]+[.]css)"/g)].map(
      (match) => match[1],
    )
    assert.ok(styles.length > 0, 'Brakuje arkusza stylów interfejsu.')
    for (const path of styles) {
      const css = await fetch(uiBase + path)
      assert.equal(css.status, 200)
      assert.match(css.headers.get('content-type'), /text[/]css/)
      assert.ok((await css.text()).length > 100)
    }
    for (const node of ['a', 'b', 'c']) {
      assert.equal(
        (await get(uiBase, '/nodes/' + node + '/cluster/status')).id,
        node,
      )
    }
    for (const node of ['a', 'b', 'c']) {
      const blocked = await fetch(uiBase + '/nodes/' + node + '/cluster/internal/vote', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: '{}',
      })
      assert.equal(blocked.status, 403)
    }
    const blockedDirect = await fetch(bases[0] + '/cluster/internal/append', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}',
    })
    assert.equal(blockedDirect.status, 403)
    const [alice, bob, charlie] = await Promise.all([
      identity(),
      identity(),
      identity(),
    ])

    // T1–T6: all commands are signed, but rejected commands do not consume a nonce.
    const issueBasic = await operation(alice, 'issue', '1001', alice.address, 1)
    assert.equal(
      (await post(uiBase, '/nodes/a/cluster/operations', issueBasic)).body
        .status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1001', alice.address)
    const t2 = await get(bases[1], '/cluster/assets/1001')
    assert.equal(t2.owner, alice.address)
    assert.equal(t2.status, 'ACTIVE')
    const basicTransfer = await operation(
      alice,
      'transfer',
      '1001',
      bob.address,
      2,
    )
    assert.equal(
      (await post(bases[0], '/cluster/operations', basicTransfer)).body.status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1001', bob.address)
    const forbidden = await operation(
      charlie,
      'transfer',
      '1001',
      charlie.address,
      1,
    )
    assert.equal(
      (await post(bases[2], '/cluster/operations', forbidden)).body.status,
      'REJECTED',
    )
    await assetEverywhere(bases, '1001', bob.address)
    const redeem = await operation(bob, 'redeem', '1001', '', 1)
    assert.equal(
      (await post(bases[1], '/cluster/operations', redeem)).body.status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1001', bob.address, 'REDEEMED')
    const redeemAgain = await operation(bob, 'redeem', '1001', '', 2)
    const transferRedeemed = await operation(
      bob,
      'transfer',
      '1001',
      charlie.address,
      2,
    )
    assert.equal(
      (await post(bases[1], '/cluster/operations', redeemAgain)).body.status,
      'REJECTED',
    )
    assert.equal(
      (await post(bases[1], '/cluster/operations', transferRedeemed)).body
        .status,
      'REJECTED',
    )
    await assetEverywhere(bases, '1001', bob.address, 'REDEEMED')
    console.log('T1–T6: tworzenie, odczyt, transfer, autoryzacja i REDEEM')

    // T7–T8: only the client holds each non-extractable private key.
    const issueSigned = await operation(
      alice,
      'issue',
      '1002',
      alice.address,
      3,
    )
    assert.equal(
      (await post(bases[0], '/cluster/operations', issueSigned)).body.status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1002', alice.address)
    const signed = await operation(alice, 'transfer', '1002', bob.address, 4)
    const unsigned = { ...signed, signature: '' }
    assert.equal(
      (await post(bases[1], '/cluster/operations', unsigned)).body.status,
      'REJECTED',
    )
    const otherSignature = await operation(
      charlie,
      'transfer',
      '1002',
      bob.address,
      1,
    )
    assert.equal(
      (
        await post(bases[1], '/cluster/operations', {
          ...signed,
          signature: otherSignature.signature,
        })
      ).body.status,
      'REJECTED',
    )
    await assetEverywhere(bases, '1002', alice.address)
    assert.equal(
      (await post(bases[1], '/cluster/operations', signed)).body.status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1002', bob.address)
    assert.equal(
      (await post(bases[2], '/cluster/operations', signed)).body.status,
      'REJECTED',
    )
    await assetEverywhere(bases, '1002', bob.address)
    console.log('T7–T8: podpis użytkownika wymagany; ponowne użycie odrzucone')

    // T9–T10: complete ordered history and independent state read from another node.
    const issueHistory = await operation(
      alice,
      'issue',
      '1003',
      alice.address,
      5,
    )
    const aToB = await operation(alice, 'transfer', '1003', bob.address, 6)
    const bToC = await operation(bob, 'transfer', '1003', charlie.address, 2)
    const cRedeems = await operation(charlie, 'redeem', '1003', '', 1)
    for (const op of [issueHistory, aToB, bToC, cRedeems]) {
      assert.equal(
        (await post(bases[0], '/cluster/operations', op)).body.status,
        'SUCCESS',
      )
    }
    await assetEverywhere(bases, '1003', charlie.address, 'REDEEMED')
    const history = await get(bases[1], '/cluster/history/1003')
    assert.deepEqual(
      history.map((item) => item.type),
      ['issue', 'transfer', 'transfer', 'redeem'],
    )
    assert.deepEqual(
      history.map((item) => item.operationId),
      [issueHistory, aToB, bToC, cRedeems].map((item) => item.operationId),
    )
    assert.deepEqual(
      history.map((item) => item.sender),
      [alice.address, alice.address, bob.address, charlie.address],
    )
    assert.deepEqual(
      history.map((item) => item.recipient),
      [alice.address, bob.address, charlie.address, ''],
    )
    const independent = await get(bases[2], '/cluster/assets/1003')
    assert.equal(independent.owner, charlie.address)
    assert.equal(independent.status, 'REDEEMED')
    console.log(
      'T9–T10: chronologiczna historia i niezależny odczyt z drugiego węzła',
    )

    // T11–T12: independent durable nodes, failure and automatic catch-up.
    const issueReplicated = await operation(
      alice,
      'issue',
      '1004',
      alice.address,
      7,
    )
    assert.equal(
      (await post(bases[0], '/cluster/operations', issueReplicated)).body
        .status,
      'SUCCESS',
    )
    const transferReplicated = await operation(
      alice,
      'transfer',
      '1004',
      bob.address,
      8,
    )
    assert.equal(
      (await post(bases[0], '/cluster/operations', transferReplicated)).body
        .status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1004', bob.address)
    console.log('T11: trzy węzły mają ten sam stan')

    await compose(env, 'stop', '-t', '2', 'node-a')
    await readyLeader([bases[1], bases[2]])
    const transferAfterFailure = await operation(
      bob,
      'transfer',
      '1004',
      charlie.address,
      3,
    )
    assert.equal(
      (await post(bases[1], '/cluster/operations', transferAfterFailure)).body
        .status,
      'SUCCESS',
    )
    await assetEverywhere([bases[1], bases[2]], '1004', charlie.address)
    await compose(env, 'start', 'node-a')
    await assetEverywhere(bases, '1004', charlie.address)
    console.log(
      'T12: operacja przy awarii i automatyczna synchronizacja po powrocie',
    )

    // T13: concurrent conflicting signatures with the same nonce.
    const issueConflict = await operation(
      alice,
      'issue',
      '1005',
      alice.address,
      9,
    )
    assert.equal(
      (await post(bases[1], '/cluster/operations', issueConflict)).body.status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1005', alice.address)
    const first = await operation(alice, 'transfer', '1005', bob.address, 10)
    const second = await operation(
      alice,
      'transfer',
      '1005',
      charlie.address,
      10,
    )
    const outcomes = await Promise.all([
      post(bases[1], '/cluster/operations', first),
      post(bases[2], '/cluster/operations', second),
    ])
    assert.equal(
      outcomes.filter((result) => result.body.status === 'SUCCESS').length,
      1,
    )
    assert.equal(
      outcomes.filter((result) => result.body.status === 'REJECTED').length,
      1,
    )
    const winner =
      outcomes[0].body.status === 'SUCCESS' ? bob.address : charlie.address
    await assetEverywhere(bases, '1005', winner)
    const conflictHistory = await get(bases[0], '/cluster/history/1005')
    assert.equal(conflictHistory.length, 2)
    console.log(
      'T13: dokładnie jeden z dwóch konfliktujących transferów zatwierdzono',
    )

    // T14 uses another fresh asset so Alice owns the starting state.
    const issuePartition = await operation(
      alice,
      'issue',
      '1006',
      alice.address,
      11,
    )
    assert.equal(
      (await post(bases[2], '/cluster/operations', issuePartition)).body.status,
      'SUCCESS',
    )
    await assetEverywhere(bases, '1006', alice.address)
    const leaderBeforePartition = await readyLeader(bases)
    const ids = ['a', 'b', 'c']
    const minorityIndex = ids.indexOf(leaderBeforePartition.id)
    assert.notEqual(minorityIndex, -1)
    const majorityIndices = [0, 1, 2].filter((index) => index !== minorityIndex)
    await Promise.all(
      bases.map((base, index) =>
        post(base, '/cluster/test/partition', {
          blockedPeers:
            index === minorityIndex
              ? majorityIndices.map((i) => ids[i])
              : [ids[minorityIndex]],
        }),
      ),
    )
    await readyLeader(majorityIndices.map((index) => bases[index]))
    const isolated = await operation(alice, 'transfer', '1006', bob.address, 12)
    const majority = await operation(
      alice,
      'transfer',
      '1006',
      charlie.address,
      12,
    )
    const partitionResults = await Promise.all([
      post(bases[minorityIndex], '/cluster/operations', isolated),
      post(bases[majorityIndices[0]], '/cluster/operations', majority),
    ])
    assert.notEqual(partitionResults[0].body.status, 'SUCCESS')
    assert.equal(partitionResults[1].body.status, 'SUCCESS')
    await Promise.all(
      bases.map((base) =>
        post(base, '/cluster/test/partition', { blockedPeers: [] }),
      ),
    )
    await assetEverywhere(bases, '1006', charlie.address)
    const partitionHistory = await get(bases[0], '/cluster/history/1006')
    assert.equal(partitionHistory.length, 2)
    console.log('T14: po przywróceniu komunikacji węzły wskazują jeden stan')
  } catch (error) {
    const diagnostics = await Promise.all(
      bases.map(async (base) => {
        const status = await get(base, '/cluster/status').catch(() => null)
        const asset = await get(base, '/cluster/assets/1001').catch(() => null)
        return { base, status, asset }
      }),
    )
    console.error('Stan testowych węzłów:', JSON.stringify(diagnostics))
    throw error
  } finally {
    if (created) await compose(env, 'down', '--volumes', '--remove-orphans')
  }
})
