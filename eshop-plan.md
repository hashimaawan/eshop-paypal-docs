# PayPal Integration Plan — eShop

> Integrate PayPal as the real payment provider for the dotnet/eShop reference
> application, replacing the simulated payment step. This plan describes **what**
> each use case does and the **flow** it follows — not where any of it is wired in
> (that is supplied separately at implementation time) and not any concrete API
> endpoint shapes. Every use case maps onto functionality eShop **already has**;
> nothing here adds new shopper- or merchant-facing capability to eShop. We are
> integrating, not extending.

---

## 1. Overview

### 1.1 What this is

eShop already runs a complete order lifecycle — a shopper places an order, the
order waits out a grace period, stock is validated, the order is "paid," and it
can then be shipped or cancelled. Today the **payment** in the middle of that
lifecycle is a simulation: a single configured boolean decides whether the order
"succeeds" or "fails," and no money, gateway, or card is ever involved. The
checkout form collects only a shipping address and submits placeholder card data.

This integration swaps that simulation for a **real PayPal payment**, threaded
through the exact lifecycle eShop already has. The shopper genuinely approves a
payment with PayPal; funds are genuinely held and later captured at the moment
eShop already designates as "payment"; and when eShop already cancels an order,
the held funds are genuinely released.

### 1.2 The one design decision everything follows from: authorize-then-capture

eShop does **not** take payment at checkout. It places the order first, then runs
a grace period and a stock-validation step, and only *after stock is confirmed*
does it perform "payment." This deferral is the defining shape of eShop's
lifecycle, and it dictates the PayPal model:

- **At checkout**, the shopper approves the payment and we **authorize** it — funds
  are *held*, not taken. Nothing is captured while the order is still unvalidated.
- **At the existing payment moment** (stock confirmed), we **capture** the held
  authorization — money moves exactly when eShop says payment happens.
- **If the order is cancelled before that moment** (stock rejected, or a
  pre-payment cancellation), we **void** the hold — money never moved, and the
  reservation is released.

This is the *only* faithful mapping. Immediate capture at checkout is explicitly
rejected (see §8): it would take money before eShop has validated stock, and
eShop has **no** post-payment reversal path to refund a captured payment whose
order is later rejected — so captured-then-rejected funds would be stranded.
Authorize-then-capture is what makes a real payment fit eShop's existing flow
without adding anything to eShop.

### 1.3 The core loop (every use case is a slice of this)

```
  Shopper places order (existing checkout)
        │
        ▼
  Create PayPal order from the cart  ──▶  Shopper approves at PayPal
        │                                        │
        │                                        ▼
        │                                  Authorize  (funds HELD, not taken)
        ▼
  Order enters eShop's normal lifecycle, carrying the PayPal references
        │
        ├─ grace period ─▶ stock validation
        │
        ├─ stock CONFIRMED ─▶  CAPTURE the hold  ─▶ success → order Paid
        │                                          └ failure → order Cancelled (existing path)
        │
        ├─ stock REJECTED  ─▶  VOID the hold  ─▶ order Cancelled (existing path)
        │
        └─ cancelled before capture ─▶ VOID the hold ─▶ funds released
```

### 1.4 What carries the integration

Three PayPal identifiers — the **order id**, the **authorization id**, and the
**capture id** — are produced across the flow and travel with the eShop order so
that a later capture, re-authorization, or void can find the right PayPal object.
They are recorded against the order as it moves through the lifecycle. (This plan
does not say *where* they are stored — only that the order must carry them.)

---

## 2. Use Cases

Five use cases, each a real flow over eShop's existing lifecycle. Each has an
actor, a step-by-step flow, and explicit success/failure states — the failure
state always lands on a path eShop already has.

> **Naming used below (eShop's existing lifecycle, in business terms):** *order
> placed* → *grace period* → *stock validation* → *stock confirmed / stock
> rejected* → *paid* → *shipped*; with *cancellation* permitted only before *paid*.
> No service, event, file, or endpoint names appear here by design.

---

### UC1 — Checkout with PayPal approval + authorization hold

The shopper pays for an order through PayPal at checkout. This replaces the
placeholder card data the checkout submits today; the order-placement action
itself is unchanged — it simply now carries a real, approved payment.

**Actor:** Shopper (authenticated buyer).

**Flow:**
1. The shopper reaches checkout with a non-empty cart and the shipping address
   eShop already collects.
2. A **PayPal order is created** for the cart, with **authorize intent**. The
   amount is eShop's existing order total (a single item total — eShop models no
   separate tax or shipping lines, so none are sent), in the store's configured
   currency. The shipping address eShop collected is passed through so the shopper
   sees consistent details at PayPal.
3. The shopper is taken to PayPal to **approve** the payment (redirect or embedded
   button — see §6).
4. On approval, the backend **authorizes** the payment: PayPal places a **hold**
   on the funds. No money has moved.
5. The eShop order is placed, now carrying the PayPal order id and authorization
   id, and enters the normal lifecycle (grace period → stock validation).

**Success state:** Order placed with funds held; order proceeds exactly as eShop
orders do today.

**Failure state:**
- Shopper abandons or cancels approval at PayPal → no authorization, the order is
  not placed, the cart is preserved, the shopper is returned to checkout.
- Authorization is declined (e.g. insufficient funds, card issue) → the order is
  not placed; the shopper is told the payment could not be approved.

---

### UC2 — Capture at the payment moment (stock confirmed)

This is the heart of the integration: the simulated success/fail boolean becomes a
**real capture** of the funds held in UC1, performed at the exact moment eShop
already calls "payment."

**Actor:** System (triggered by eShop reaching the payment moment).

**Flow:**
1. The order clears the grace period and stock validation and arrives at the
   payment moment with stock **confirmed**.
2. The backend **captures** the held authorization for the order's full amount,
   using the authorization id carried on the order.
3. On success → the payment is settled and the order becomes **Paid**, continuing
   into eShop's existing post-payment flow (ship/cancel rules unchanged).
4. On failure (decline, insufficient funds, an authorization that can no longer be
   captured) → this maps onto eShop's **existing payment-failure path**, which
   already cancels the order. No new failure behavior is introduced.

**Success state:** Funds captured; order Paid.
**Failure state:** Capture fails → order follows eShop's existing payment-failed →
cancelled transition.

**Note:** Exactly one capture is performed, for the full order amount. eShop has no
notion of partial or multiple captures, so neither is used.

---

### UC3 — Re-authorization within the capture window

Because eShop inserts a grace period and a stock-validation step *between* approval
and capture, the hold from UC1 can age before UC2 runs. PayPal authorizations are
**honored for 3 days** and remain **valid for up to 29 days**; capturing after the
honor window may require a fresh authorization first. This use case handles that
real PayPal constraint at the same payment moment — it adds nothing to eShop, it
just makes the capture robust.

**Actor:** System (at the payment moment, as part of UC2).

**Flow:**
1. At the payment moment, before capturing, check the age of the held
   authorization.
2. If it is **within the honor window** → capture directly (UC2).
3. If it is **past the honor window but within validity** → **re-authorize** to
   obtain a fresh hold, then capture against the new authorization id (which now
   travels with the order).
4. If it is **past validity** → the funds can no longer be secured; treat this as a
   capture failure → eShop's existing payment-failed → cancelled path (as in UC2).

**Success state:** A current authorization is captured; order Paid.
**Failure state:** No valid authorization can be obtained → order cancelled via the
existing path.

---

### UC4 — Void on stock rejection (automatic)

When stock validation rejects an order, eShop **already** cancels it. Because no
capture has happened yet, the corresponding PayPal hold must be released so the
shopper's reserved funds are freed.

**Actor:** System (triggered by eShop rejecting stock and cancelling the order).

**Flow:**
1. Stock validation rejects one or more items; eShop cancels the order (existing
   behavior).
2. The backend **voids** the authorization carried on the order.
3. The hold is released; no money ever moved.

**Success state:** Authorization voided; order cancelled with no charge.
**Failure state:** If the void cannot be performed, the order is still cancelled by
eShop (billing/notification failures never block eShop's lifecycle); the
discrepancy is logged for reconciliation, and an un-captured authorization expires
on its own.

---

### UC5 — Void on cancellation before payment

eShop allows an order to be cancelled only **before it is paid**. Any such
cancellation must release the PayPal hold, mirroring UC4 but triggered by a
shopper- or back-office-initiated cancellation rather than stock rejection.

**Actor:** Shopper or back-office operator (whoever triggers eShop's existing
cancellation), before the payment moment.

**Flow:**
1. The order is cancelled while still **pre-payment** (placed / awaiting validation
   / stock-confirmed but not yet captured).
2. The backend **voids** the authorization carried on the order.
3. The hold is released; funds return to the shopper.

**Success state:** Authorization voided; order cancelled with no charge.
**Failure state:** Void failure handled as in UC4 (order still cancelled;
discrepancy logged; un-captured hold lapses naturally).

**Constraint — and why it already fits:** voiding only works on an authorization
that has **not** been captured. eShop **already forbids** cancelling a Paid or
Shipped order. The two rules line up exactly: by the time eShop won't let you
cancel, PayPal won't let you void — and there is deliberately no refund path here
(see §8), because eShop has no post-payment reversal to attach one to.

---

## 3. Cross-cutting flows

These apply across all use cases and complete each one's behavior.

### 3.1 Failure isolation and reconciliation
- A **declined authorization** (UC1) stops order placement at checkout — the
  shopper learns the payment wasn't approved before any order exists.
- A **failed capture** (UC2/UC3) routes to eShop's existing payment-failed →
  cancelled path; a hold that was placed but never captured lapses on its own.
- A **failed void** (UC4/UC5) never blocks eShop's cancellation: the order is
  cancelled regardless, the failure is logged, and the un-captured hold expires
  naturally. PayPal calls are notification-and-settlement around eShop's
  lifecycle; they never roll the lifecycle back.

### 3.2 Idempotency
Checkout and the payment moment can be retried (network retries, duplicate
submits). Each money-moving PayPal operation is made **idempotent** using a stable
request identifier so a retry never authorizes, captures, or voids twice. eShop
already issues a per-checkout request id and already de-duplicates order creation;
the PayPal operations reuse that same idea so retries are safe end to end.

### 3.3 Amount and order mapping
- The PayPal amount equals eShop's **existing order total** (sum of line items).
- eShop models **no separate tax or shipping charge**, so the breakdown is a single
  item total — none are invented.
- A **currency** must accompany every amount; since eShop does not carry a currency
  code on the order, the store currency is configured once (defaulting to the
  store's operating currency) and applied to every PayPal amount.
- The **shipping address** eShop collects is passed to PayPal so the shopper sees
  matching details during approval.

---

## 4. Authentication

- Server-to-server **OAuth 2.0 client-credentials** with PayPal; access tokens are
  acquired and refreshed by the integration, never exposed to the storefront.
- Credentials and the **sandbox/production** toggle come from configuration/
  environment — sandbox for all development and testing, production only when live.
- No card data ever touches eShop: the shopper authenticates and approves with
  PayPal directly, which is the whole point of replacing the placeholder card
  fields.

---

## 5. State carried on the order

The integration is stateless except for the PayPal references that must follow the
order through its lifecycle:

| Reference | Produced at | Used by |
| --- | --- | --- |
| PayPal **order id** | UC1, on order creation | Approval correlation, authorization |
| **Authorization id** | UC1 (and refreshed in UC3) | UC2 capture, UC4/UC5 void, UC3 re-auth |
| **Capture id** | UC2, on successful capture | Settlement record on the Paid order |

This plan specifies only that the order **carries** these; the storage mechanism
and location are deliberately out of this document.

---

## 6. Buyer approval surface

UC1 requires the shopper to approve at PayPal. Two supported options, both ending
at the same backend authorize step:

- **Redirect flow** — the backend returns a PayPal approval link; the storefront
  sends the shopper to PayPal and handles the return with the approved order id.
- **Embedded flow** — PayPal's in-page buttons handle approval in the browser; the
  storefront hands the approved order id back to the backend.

Either way the storefront's checkout gains an **approval step** where today it
silently submits placeholder card data. This is the one shopper-visible change, and
it is part of the existing place-order action — not a new feature.

---

## 7. Testing strategy

- All flows run against **PayPal Sandbox** with test buyer and merchant accounts.
- Each use case has a **happy path** and at least one **failure path**:
  - UC1: approved vs. declined/abandoned authorization.
  - UC2: successful capture vs. declined capture (→ existing cancel path).
  - UC3: capture inside honor window vs. past honor (re-auth) vs. past validity
    (→ cancel).
  - UC4/UC5: successful void vs. void-failure isolation.
- **Negative scenarios** (declines, insufficient funds, un-captureable
  authorizations, double-submit/idempotency) are exercised using PayPal sandbox's
  simulation facilities and duplicate-request replays.
- Money-moving operations are mocked in unit tests and run for real against sandbox
  in integration tests; eShop's existing lifecycle assertions (order reaches Paid;
  rejected/cancelled orders end cancelled) are reused as the outer checks.

---

## 8. Use-case ↔ existing-eShop-functionality map

| UC | PayPal operation | eShop functionality it integrates onto | Adds new eShop behavior? |
| --- | --- | --- | --- |
| UC1 | Create order + authorize | Checkout / place-order (replaces placeholder card) | No |
| UC2 | Capture | The existing payment moment at stock-confirmed | No |
| UC3 | Re-authorize then capture | Same payment moment (robustness only) | No |
| UC4 | Void | Existing auto-cancel on stock rejection | No |
| UC5 | Void | Existing pre-payment cancellation | No |

---

_Industry: e-commerce checkout — a real PayPal payment threaded through eShop's
existing authorize-deferred, capture-on-stock-confirmed order lifecycle, releasing
held funds wherever eShop already cancels._
