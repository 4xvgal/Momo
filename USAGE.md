# Usage

## Setup

1. Open Store Settings → Lightning.
2. Click **Change connection**.
3. Pick **LNURL backend**.
4. Enter your Lightning Address: `you@wallet.com`.
5. Click **Test connection**.
6. Save.

Payments go straight to your wallet. BTCPayServer only verifies them.

## Notes

- Your provider must support LUD-21 verify.
- Refunds are manual only. This backend holds no node.
- Your address network must match the BTCPayServer network (mainnet, testnet, regtest).

## Errors

| Message | Meaning | Fix |
|---|---|---|
| `HTTPS required` | Provider URL is not HTTPS. | Use an HTTPS provider. |
| `Blocked IP range` / `DNS resolved to private IP` | Provider points to a private network. | Use a public provider. This is the SSRF guard. |
| `does not support LUD-21 verify` | Provider cannot confirm payments. | Pick a provider with LUD-21 (Alby, Blink...). |
| `LNURL endpoint does not support payRequest` | Address is not LNURL-pay. | Check the address is correct. |
| `Amount ... outside the LNURL range` | Amount exceeds provider limits. | Adjust the invoice amount. |
| `BOLT11 amount mismatch` | Provider returned a wrong amount invoice. | Provider issue. Contact them. |
| `description_hash does not match` | Invoice does not match the address metadata. | Provider issue. Contact them. |
| `Invalid BOLT11: Invalid prefix` | Network mismatch. | Mainnet address on a regtest instance, or the reverse. |
| `Preimage does not match payment_hash` | Suspicious verify response. | Logged, payment ignored. Possible spoofing. |

## Verify a payment

- Checkout shows the invoice from your provider.
- The plugin polls the verify URL every 5 seconds.
- Invoice turns Paid when settled and the preimage is valid.
