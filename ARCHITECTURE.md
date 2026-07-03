# Architecture & Design — Rate Limiter

This document is both the **system design** for this project and an **interview study sheet**.
It follows the classic "Rate Limiter" system-design breakdown and maps each idea to what is
actually implemented in this repo.

---

## 1. Overview & Problem

A rate limiter is a **middleware layer that controls the amount of incoming traffic to an API**.
It protects downstream services from:

- **DDoS / traffic spikes** — caps how fast any one client can hit you
- **Brute-force attempts** — slows credential-stuffing and enumeration
- **Resource starvation** — stops one heavy client from consuming all capacity ("noisy neighbour")
- **Cost control** — for paid/metered downstreams (LLM APIs, SMS, etc.)

**Real-world examples:** Stripe API, GitHub API, OpenAI API — all return `429 Too Many Requests`
with a `Retry-After` header when you exceed your quota.

---

## 2. System Design Concepts

| Concept | Where it appears here |
|---|---|
| **Token Bucket** | Default algorithm — refills tokens at a steady rate, allows bursts |
| **Fixed / Sliding Window** | Selectable algorithms |
| **Concurrency limiting** | Selectable algorithm — caps in-flight requests |
| **Partitioning / multi-tenancy** | Per-`X-Api-Key` (or per-IP) independent buckets |
| **Distributed systems** | Discussed in §9 (single-node here; Redis for scale-out) |
| **Fail-open vs fail-closed** | Discussed in §9 |
| **Atomicity / race conditions** | Handled in-process here; Lua+Redis for distributed (see §9) |

---

## 3. Algorithms (pros & cons)

### Token Bucket ✅ *(default in this project)*
A bucket holds up to `TokenLimit` tokens; each request spends one; tokens refill at
`TokensPerPeriod` every `ReplenishmentPeriod`.
- **Pro:** allows controlled bursts, smooth average rate, memory-cheap (a count + timestamp).
- **Con:** two params to tune (capacity vs refill).

### Fixed Window
Count requests per fixed clock window (e.g. 100/min).
- **Pro:** trivial to implement and reason about.
- **Con:** **edge-burst** — a client can send `limit` at 00:59 and `limit` again at 01:00,
  i.e. 2× the limit across a window boundary.

### Sliding Window
Splits the window into segments and weights the previous window.
- **Pro:** removes the fixed-window edge burst; smoother.
- **Con:** more state and computation than fixed window.

### Concurrency
Limits *simultaneous* requests, not requests-per-time.
- **Pro:** protects a fixed-capacity downstream (e.g. a thread/connection pool).
- **Con:** doesn't cap total throughput over time.

---

## 4. Tech Stack

| Layer | This project | Typical production alternative |
|---|---|---|
| Gateway / interceptor | ASP.NET Core rate-limiting **middleware** | Nginx / Envoy / API Gateway, or Express middleware |
| Datastore | **In-memory** (per node) | **Redis** (memory-optimised, shared across nodes) |
| Atomic ops | In-process lock (framework-managed) | **Lua scripts** in Redis (atomic check-and-decrement) |

This repo is intentionally single-node so the algorithm is easy to read and test. The
distributed design is described in §7 and §9 so it can be discussed in an interview.

---

## 5. Implementation Roadmap

Where this repo sits on the classic learning path:

1. ✅ Build a simple API with a dummy payload → `GET /api/ping`
2. ✅ Implement a basic in-memory **Fixed Window** counter
3. ⬜ Identify race conditions in memory and move state to Redis *(distributed step)*
4. ✅ Implement the **Token Bucket** algorithm *(default)*
5. ✅ Implement the **Sliding Window** algorithm
6. ⬜ Write **Lua scripts** for atomic increments in Redis *(distributed step)*
7. ✅ Return standard **HTTP 429** headers
8. ✅ Include metadata headers in responses → `Retry-After` (and `X-RateLimit-*` extensible)

Steps 3 & 6 are the natural "next PR" and make a strong talking point: *"single-node today,
here's exactly how I'd shard it across a cluster."*

---

## 6. State / Data Model

**In-process (this repo):** the framework keeps a small structure **per partition key** —
essentially the current token count and last-refill timestamp. Memory is O(number of active clients).

**Distributed (Redis KV — no relational schema needed):**

```
Key:   rate_limit:{client_id}:{endpoint}
Value: current token count (integer)
TTL:   window / replenishment duration (e.g. 60 seconds)
```

A key-value store with per-key TTL fits perfectly: entries expire automatically, lookups are O(1),
and a relational schema would add join/transaction overhead for no benefit.

---

## 7. High-Level Architecture

**This project (single node):**

```
          [ Client ]
              │  (X-Api-Key header)
              ▼
   ┌─────────────────────────┐
   │  ASP.NET Core pipeline   │
   │  ┌───────────────────┐  │
   │  │ RateLimiter        │  │  ── partition by client ─► in-memory bucket
   │  │ middleware         │  │      allow ──► next
   │  └───────────────────┘  │      deny  ──► 429 + Retry-After
   │  ┌───────────────────┐  │
   │  │ PingController      │ │
   │  └───────────────────┘  │
   └─────────────────────────┘
```

**Scaled out (distributed):**

```
                    [ Client ]
                        │
                        ▼
   [ API Gateway / Rate Limiter ]  ◄──►  [ Redis Cluster ]
                        │  (if allowed)
                        ▼
          [ Internal Backend Services ]
```

The limiter check hits a shared Redis so **every node enforces one global limit** rather than
each node allowing the full quota independently.

---

## 8. Request Flow

1. Request arrives; middleware runs **before** any controller.
2. Derive the **partition key**: `X-Api-Key` if present, else client IP.
3. Look up / create that partition's limiter for the configured algorithm.
4. **Acquire a permit:**
   - **Allowed** → forward to the endpoint.
   - **Denied** → short-circuit with **HTTP 429** + `Retry-After`.

A Rate Limiter is an **interceptor/middleware**, not a standalone endpoint — it intercepts
`* /*` (all routes), which is why the logic lives in `RateLimiterSetup`, not in a controller.

---

## 9. Interview Questions (with answers)

**Q1. Fixed Window vs Sliding Window — pros and cons?**
Fixed window is simplest (one counter per window) but suffers the **edge-burst**: a client can
send `limit` requests just before the boundary and `limit` again just after — 2× the intended
rate. Sliding window divides the window into segments and weights the prior window to smooth this
out, at the cost of more state and computation.

**Q2. Why Redis instead of a relational database?**
Rate limiting is a high-frequency, read-modify-write, **short-lived** workload. Redis gives O(1)
in-memory ops, **atomic** increments, and **per-key TTL** for automatic expiry. A relational DB
adds transaction/lock overhead and disk I/O for data that lives for seconds — wrong tool.

**Q3. How do you solve race conditions when many requests hit the limiter at once?**
Single node: the framework serialises access to each partition's counter in-process. Distributed:
wrap the check-and-decrement in a **Lua script** executed by Redis. Redis runs the script
atomically, so "read count → compare → decrement" cannot interleave between nodes.

**Q4. How does rate limiting work in a distributed microservices architecture?**
Move the counter out of process into a **shared store (Redis)** keyed by
`client:endpoint`, and do the check at the **API gateway** so it's enforced once, before fan-out.
Otherwise each of N nodes allows the full quota → effective limit becomes N× intended.

**Q5. How do you handle Redis failures — fail open or fail closed?**
A trade-off: **fail-open** (allow traffic if Redis is down) favours availability but removes
protection during the outage; **fail-closed** (reject) favours protection but can cause a
self-inflicted outage. Common choice: **fail-open with alerting** for public traffic, **fail-closed**
for sensitive/expensive endpoints (auth, billing). This project logs and would fail-open by default.

**Q6. How would you return quota info to clients?**
Standard headers: `Retry-After` (implemented) and the `X-RateLimit-Limit` /
`X-RateLimit-Remaining` / `X-RateLimit-Reset` family so well-behaved clients self-throttle.

---

## 10. Bonus Features (extension ideas)

- **Multi-tenant tiers** — different limits for Free vs Premium (choose settings by API-key claim).
- **IP-based throttling** — already supported via `PartitionBy: IpAddress`.
- **Soft vs hard limits** — warn/log at a soft threshold, reject at the hard limit.
- **`X-RateLimit-Remaining`** metadata headers on every response.

---

## 11. Key Learning Outcomes

- Distributed **concurrency control** and why atomicity matters.
- Implementing and contrasting **Token Bucket / Fixed / Sliding Window / Concurrency**.
- **API gateway / interceptor** patterns and the 429 + `Retry-After` contract.
- Config-driven, testable design (options binding + `WebApplicationFactory` integration tests).
