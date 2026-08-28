# MOMO - Lightning Address as Payment method.

![momo_banner](misc/momo_3_1.png)

[BTCPay Server](https://github.com/btcpayserver/btcpayserver) plugin that turns an external **Lightning Address (LUD-06/16)** into a "virtual Lightning backend". merchant 
can register the Lightning Address of the wallet to setup virtual LN node. (Alby, Blink etc)

## How it works

1. Checkout asks the registered Lightning Address for a BOLT11 invoice matching the order amount (LUD-06 `payRequest`).
2. The customer pays the invoice directly to the merchant's wallet.
3. The plugin confirms the payment by polling the LUD-21 `verify` endpoint — no node events, no webhooks.

```
[Customer] --invoice--> [BTCPayServer Checkout]
                            |
                            v
              LUD-06 payRequest -> { bolt11, verifyUrl }
                            |
         [customer pays bolt11 -> merchant wallet]
                            |
              LUD-21 verify polling -> settled
```

## Features

- Non-custodial: BTCPayServer holds no preimage, no channels, no funds
- Merchant onboarding = one Lightning Address in store settings
- Works with any wallet/provider that supports LUD-06 and LUD-21

## Roadmap: non-custodial processors

**Goal** — merchant registers an identifier; the plugin derives a per-order payment target from it and confirms settlement, holding no keys or funds.

**Design principle** — in every LN flow the plugin itself fetches the one-time invoice from the merchant's offer/endpoint. That binds order, exact amount and expiry at creation (same shape as the BOLT11 flow today), leaving settlement detection as the open problem.

### Shipped
- [x] **BOLT11 via LNURL** (LUD-06 payRequest + LUD-21 verify polling)

### Open

- [ ] **BIP353 (`user@domain`)** — discovery layer only: DNS TXT → `bitcoin:` URI. One name can point to any rail below — but every rail below also works standalone, without BIP353.
  - [ ] DNS TXT publishing — the merchant (own domain: manual copy-paste of the offer/address) or the wallet provider (hosted address: automated issuance not common yet)
  - [ ] **→ BOLT12 offer (`lno:`)** — standalone: merchant pastes a raw `lno1...` offer into store settings
    - [ ] no standard HTTP spec for offer→invoice fetch — per-backend adapters meanwhile (NWC, Phoenixd-style REST)
    - [ ] no LUD-21 equivalent — settlement detection is per-backend (invoice lookup / payment notifications) until a standard exists
    - [ ] nodeless onion-message client doesn't exist — invoice_request direct to the blinded path needs a node stack (CLN is the only full BOLT12 implementation)
    - [ ] customer wallet support for paying BOLT12 invoices (varies)
    - [ ] payer proofs ([bolts#1346](https://github.com/lightning/bolts/pull/1346)) — spec merged, wallets don't issue them yet; once shipped this replaces the per-backend settlement adapters above
  - [ ] **→ on-chain address** — standalone: merchant pastes a raw `bc1...` address into store settings
    - [ ] static address reuse (privacy/traceability — every order pays the same address)
    - [ ] no invoice and no preimage ever reach the plugin — detection depends entirely on the backend on-chain API (address/tx lookup, confirmation depth)
  - [ ] **→ silent payments (BIP352)** — standalone: merchant pastes a raw `sp1q...` address into store settings
    - [ ] no invoice and no preimage ever reach the plugin — detection depends entirely on the backend on-chain API (address/tx lookup, confirmation depth)

## Non-goals

- Custodial verification paths — every processor above must work without the backend holding keys
- **Automatic refunds** — the backend holds no node, so refunds are manual only (stated in the store settings UI and checkout)

## Requirements

- .NET SDK 10.0 or later
- BTCPay Server 2.4.x (Plugin API)
- Git with submodule support
- Docker for the development environment

## Getting started

```bash
git clone --recurse-submodules https://github.com/btcpayserver/btcpayserver-plugin-template.git .
```

(Or `git submodule update --init --recursive` if you cloned without submodules.)

Then:

```bash
# build the plugin
dotnet build

# run the BTCPay Server dev environment with this plugin loaded
./plugin-env.sh
```

See [SPEC.md](SPEC.md) for the full protocol design and architecture.

## License

MIT — see [LICENSE](LICENSE).
