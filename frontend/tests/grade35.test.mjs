import assert from 'node:assert/strict'
import test from 'node:test'
import { Transaction, Wallet, parseEther, toQuantity } from 'ethers'
import { withIsolatedChain } from './local-chain.mjs'

test(
  'T7–T8: independent keys, signature and replay of the same transaction',
  { timeout: 60000 },
  async () => {
    await withIsolatedChain(async ({ provider, items }) => {
      // Keys exist only in this local process; the application database never receives them.
      const alice = Wallet.createRandom()
      const bob = Wallet.createRandom()
      const charlie = Wallet.createRandom()
      for (const user of [alice, bob, charlie]) {
        await provider.send('anvil_setBalance', [
          user.address,
          toQuantity(parseEther('1')),
        ])
      }

      await (await items.issue(alice.address, 2n)).wait()
      const transfer = {
        type: 2,
        chainId: 31337,
        nonce: 0,
        maxPriorityFeePerGas: 1_000_000_000n,
        maxFeePerGas: 10_000_000_000n,
        gasLimit: 250_000n,
        to: await items.getAddress(),
        data: items.interface.encodeFunctionData('transferFrom', [
          alice.address,
          bob.address,
          2n,
        ]),
      }

      const unsigned = Transaction.from(transfer).unsignedSerialized
      await assert.rejects(() =>
        provider.send('eth_sendRawTransaction', [unsigned]),
      )
      assert.equal(await items.ownerOf(2n), alice.address)

      const wrongSignature = await charlie.signTransaction(transfer)
      await assert.rejects(async () => {
        const tx = await provider.broadcastTransaction(wrongSignature)
        await tx.wait()
      })
      assert.equal(await items.ownerOf(2n), alice.address)

      const signed = await alice.signTransaction(transfer)
      const receipt = await (await provider.broadcastTransaction(signed)).wait()
      assert.equal(receipt.status, 1)
      assert.equal(await items.ownerOf(2n), bob.address)
      console.log(
        'T7: REJECTED without a signature and with another signature; SUCCESS with Alice signature',
      )

      await assert.rejects(() => provider.broadcastTransaction(signed))
      assert.equal(await items.ownerOf(2n), bob.address)
      const transfers = await items.queryFilter(
        items.filters.Transfer(null, null, 2n),
        0,
        'latest',
      )
      assert.equal(transfers.length, 2)
      console.log(
        'T8: REJECTED — identical signed transaction was not executed again',
      )
    })
  },
)
