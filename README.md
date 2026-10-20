# ChainLoot

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

### Install Dependencies

The project uses Git submodules to manage Solidity dependencies:

- **forge-std** — Foundry's standard library for smart contract testing.
- **OpenZeppelin Contracts** — reusable and audited implementations of Ethereum smart contracts.

If cloning the repository for the first time:

```bash
git clone --recurse-submodules <repository-url>
```

If the repository has already been cloned, navigate to its root directory and run:

```bash
git submodule update --init --recursive
```

This downloads all dependencies at the exact versions recorded in the repository.

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