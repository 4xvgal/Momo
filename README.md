# BTCPay Server Plugin: LNURL-pay Backend (Lightning Address)

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

## Non-goals

- On-chain / Liquid payments (LN only)
- Providers that don't support LUD-21 verification (no webhook path)
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
