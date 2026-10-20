# university-blockchain

## Smart Contracts — Development Setup

Smart contracts are developed using Solidity and Foundry.

### Prerequisites

- Git
- WSL2 with Ubuntu (Windows users)
- Foundry (`forge`, `cast`, `anvil`)

### Install Foundry

```bash
curl -L https://foundry.paradigm.xyz | bash
```

Add Foundry to your PATH:

```bash
export PATH="$HOME/.foundry/bin:$PATH"
```

Install the Foundry toolchain:

```bash
foundryup
```

Verify installation:

```bash
forge --version
cast --version
anvil --version
```

### Build and Test

Navigate to the smart contracts directory:

```bash
cd contracts
```

Compile smart contracts:

```bash
forge build
```

Run tests:

```bash
forge test
```

### Local Blockchain

Start a local Ethereum blockchain:

```bash
anvil
```

The local RPC endpoint is available at:

```text
http://127.0.0.1:8545
```

Verify the connection in another terminal:

```bash
cast block-number --rpc-url http://127.0.0.1:8545
```

> Anvil is intended for local development and testing. Public blockchain demonstrations use the Ethereum Sepolia testnet.
