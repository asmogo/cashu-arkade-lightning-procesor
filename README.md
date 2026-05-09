# CDK Arkade Payment Processor

> **Warning**: Not mainnet tested yet. Don't be reckless.

CDK mintd gRPC payment processor — Lightning settlements over Ark via Boltz swaps.

- **Mint**: Boltz reverse swap (Lightning → Ark VTXO)
- **Melt**: Boltz submarine swap (Ark VTXO → Lightning)

## Running locally

```bash
git submodule update --init --recursive
./submodules/NArk/regtest/start-env.sh   # starts arkd, Boltz, Fulmine, NBXplorer, LND (~3-5 min first run)

cp .env.example .env
docker compose up -d --build
```

Payment processor: `http://localhost:8080` (gRPC)  
CDK mintd: `http://localhost:3338`

## Tests

```bash
dotnet test NArkNut.Tests/NArkNut.Tests.csproj -c Release
```

## Config

| Variable | Default | |
|---|---|---|
| `PROCESSOR_WALLET_SECRET` | *(test mnemonic)* | BIP39 mnemonic for the Ark wallet |
| `PROCESSOR_WALLET_ID` | `arkade-mint` | wallet identifier |
| `ARK_URI` | `http://ark:7070` | arkd endpoint |
| `BOLTZ_URI` | `http://nginx-boltz:9069/` | Boltz REST |
| `NBXPLORER_URI` | `http://nbxplorer:32838/` | NBXplorer |
| `NBXPLORER_NETWORK` | `Regtest` | `Regtest` / `Mainnet` |

Defaults use nigiri container names. For local dev with nigiri running outside Docker, uncomment the `host.docker.internal` lines in `.env.example`.

On first boot the processor auto-creates the Ark wallet and runs DB migrations.