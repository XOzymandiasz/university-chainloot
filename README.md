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

## Frontend setup

The frontend uses Vue, TypeScript, and Vite. Run these commands in Ubuntu when using WSL.

### Prerequisites

Install Node.js 24 LTS and npm. Check your versions with:

```bash
node --version
npm --version
```

If you use [nvm](https://github.com/nvm-sh/nvm#installing-and-updating), install and select Node.js 24 with:

```bash
nvm install 24
nvm use 24
```

### Install and run

From the repository root, install the dependencies and start the development server:

```bash
cd frontend
npm ci
npm run dev
```

Open the local address printed by Vite, usually http://localhost:5173/. Keep the terminal open while using the app; press Ctrl+C to stop the server. On later runs, start the server with `npm run dev` from the `frontend` directory.

To check the production build, run:

```bash
npm run build
```

The frontend currently contains the default Vue starter page; it is not yet connected to the smart contracts.
