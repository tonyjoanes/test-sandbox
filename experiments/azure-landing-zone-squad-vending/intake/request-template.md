# Request a landing zone

File this as an issue against the platform repo. What happens next depends on which tier you
ask for — see the table below before filling this in.

| | Sandbox | Landing zone |
|---|---|---|
| Approval | Automatic — a squad lead's approval is enough, no platform review | Platform review (network peering and spend cap are real capacity/cost commitments) |
| Turnaround | Minutes (CI runs `vending/main.bicep` with `params/sandbox.bicepparam`-shaped input on merge) | Days — platform schedules the network peering half manually |
| Use it for | Prototyping, spikes, proof-of-concept, learning a new Azure service | A workload that's staying — has real users, real data, or real uptime expectations |
| Expires | Yes — see "Teardown date" below | No |

Delete this line and everything above it before submitting.

---

**Squad name:**

**Requesting tier:**
- [ ] Sandbox
- [ ] Landing zone — Corp (needs corp network / on-prem connectivity)
- [ ] Landing zone — Online (internet-facing, no corp connectivity needed)

**Owner group (Entra):** the squad group that will hold Owner on the subscription — not an
individual. If this group doesn't exist yet, create it first; this request can't be actioned
without one.

**Sponsor / cost centre:** who approves the monthly spend cap, and which cost centre it's
charged against.

**Requested monthly spend cap:** be specific — "as much as needed" gets sent back. If you don't
know yet, a sandbox default of the platform's minimum tier is used instead of blocking on this.

**Notification emails:** who gets spend-threshold alerts (`vending/modules/budget.bicep`).

### Sandbox only

**Teardown date:** sandboxes auto-expire — pick a date up to 90 days out. Needs longer? That's
usually a sign the workload should be a landing zone request instead, not a longer sandbox.

**What are you trying to find out?** One or two sentences — not for approval, just so platform
can point you at an existing sandbox or landing zone if someone's already answered this.

### Landing zone only

**Which sandbox is this graduating from, if any?** Link it. A landing zone request with no
prior sandbox isn't refused, but expect more questions about what's already been validated.

**Expected traffic / data classification:** drives SKU sizing and which policy exceptions (if
any) get reviewed alongside the request — see
`../policy/landing-zone-guardrails.bicep`.

**Network requirements:** does this workload need to reach anything on the corp network at all,
or is "Online" (no peering) actually sufficient? Answering "Online" here is usually faster to
provision and is not a lesser tier — pick based on what the workload actually needs to reach.
