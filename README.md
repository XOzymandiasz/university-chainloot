# ChainLoot

A local three-node system for issuing, transferring and redeeming digital assets. The distributed version runs entirely on this computer; no public blockchain or wallet account is required.

## Start

Prerequisites: Docker Desktop; Windows .NET SDK 9 and cached NuGet dependencies; Ubuntu/WSL with Node.js 24 and `frontend/node_modules`; and local Docker images `postgres:16-alpine`, `university-chainloot-backend:latest` and `university-chainloot-frontend:latest`. The startup script requires the WSL distribution named `Ubuntu` and tries Node.js at `~/.nvm/versions/node/v24.22.2/bin` before the regular `PATH`.

Start Docker Desktop, then run in PowerShell from the repository root:

```powershell
./scripts/start-cluster-offline.ps1
```

Open [http://127.0.0.1:5175/](http://127.0.0.1:5175/). The script uses existing dependencies and images; it does not download them. Each node uses its own PostgreSQL database.

Stop the cluster without deleting its data:

```powershell
docker compose -p chainloot-cluster -f docker-compose.cluster.yml down
```

For a clean demonstration, first stop the cluster and **only if its saved data is no longer needed**, remove its volumes with `docker compose -p chainloot-cluster -f docker-compose.cluster.yml down --volumes`, then start it again. This permanently deletes that cluster's saved assets and history.

## Demonstration

1. Open the application in three separate browser profiles. Create Alice, Bob and Charlie under **Local users**, one per profile, and copy their addresses. Each private key stays in its own browser profile; clearing that profile's data removes the key.
2. Select a node, enter an asset ID and recipient address, and use **Issue asset**, **Transfer asset**, **Redeem asset** or **Inspect asset**. The result is shown as `SUCCESS`, `REJECTED`, `FAILED` or `PENDING`; inspect the asset after a pending result before retrying.
3. For T1–T6, issue A1 to Alice, transfer it to Bob, try a transfer as Charlie, then redeem it as Bob. Check that a second redemption and a transfer both fail while Bob remains the owner.
4. For T7–T8, issue A2 to Alice, demonstrate rejection of unsigned and incorrectly signed requests, then transfer it to Bob as Alice. Use **Resend last operation** to submit the exact same signed request and check it is rejected. The automated test below performs the unsigned and wrong-signature requests; the interface does not provide buttons for them.
5. For T9–T10, issue A3 to Alice, transfer it to Bob and then Charlie, and redeem it as Charlie. Inspect its ordered history. With the main frontend closed, read the state from a node's independent database through `http://127.0.0.1:5102/cluster/assets/<id>`.
6. For T11–T12, issue A4 to Alice, transfer it to Bob through node A and inspect it through B and C. Stop A with the command below, transfer A4 from Bob to Charlie through B, and inspect C. Restart A and inspect its synchronized state.

```powershell
docker compose -p chainloot-cluster -f docker-compose.cluster.yml stop node-a
docker compose -p chainloot-cluster -f docker-compose.cluster.yml start node-a
```

7. For T13–T14, use the automated scenario below: it prepares two conflicting signed transfers, sends them through different nodes, partitions the cluster, and verifies convergence after communication is restored. T14 uses a fresh asset so Alice owns its initial state. These conflict and partition controls are not exposed as presentation buttons in the interface.

## Run all scenarios

With Docker Desktop running and the cluster images already prepared by the startup script, run from the repository root using Node.js 24 or newer:

```powershell
node --test frontend/tests/cluster.test.mjs
```

The test creates its own isolated Compose project and databases, runs T1–T14, and deletes only its own containers and test volumes. Its output reports each group of scenarios. An automated pass does not replace the live demonstration.

The separate Anvil/ERC-721 version and its tests are also included in `contracts/` and `frontend/tests/`.
