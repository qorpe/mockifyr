# Callbacks: answer now, call back later

## Why

Real payment/order flows are asynchronous: the API answers *202 Accepted* immediately, and
the result arrives later as a **webhook to your system**. A mock that can't do this leaves
half of your integration untested — usually the harder half.

## How it works in Mockifyr

A stub carries a post-serve action. When the stub matches, the response goes out first;
the webhook fires afterwards, templated from the original request:

```json
{
  "request":  { "method": "POST", "urlPath": "/api/payments/authorize" },
  "response": { "status": 202, "jsonBody": { "status": "authorizing" } },
  "postServeActions": [{
    "name": "webhook",
    "parameters": {
      "method": "POST",
      "url": "http://localhost:8080/callbacks/payment-status",
      "body": "{\"paymentId\":\"{{jsonPath originalRequest.body '$.paymentId'}}\",\"status\":\"AUTHORIZED\"}",
      "delay": { "type": "fixed", "milliseconds": 2000 }
    }
  }]
}
```

Notes: the templating model root is `originalRequest` (the request that triggered the stub);
URL, headers and body are all templated; delivery is asynchronous and fire-on-match only.

## The proof, not the promise

Delivery is captured in the request journal. The detail of the triggering request carries a
`webhooks` array:

```json
{ "url": "…/callbacks/payment-status", "delivered": true,
  "response": { "status": 200, "body": "{\"received\":true}" } }
```

If the receiver was down you get `delivered: false` plus the error — so "did the callback
fire?" is a lookup, not a debate. On the dashboard: journal detail → **Callback** tab.

In the demo the callback target is another stub on the same host, so you see **both** sides
in one journal: the authorize request (with its webhook proof) and the callback request
landing.

## The same seam, other wires

The identical post-serve mechanism drives the **`publish`** action: the stub answers the
HTTP request *and* emits a Kafka message (topic/key/body all templated). Validated against a
real broker container with the official client — the sandbox answers the call that starts a
payment and emits the event that reports it settled.
