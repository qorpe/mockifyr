# Messages: e-mail, SMS, and the OTP problem

## The idea

Your application sends **real messages over real protocols**. Mockifyr acts as the mail/SMS
server on the other end: it answers like the real provider, delivers nothing, and drops
everything into a queryable, tenant-scoped inbox.

## E-mail (SMTP)

Mockifyr runs an actual ESMTP listener (`--smtp-port 2525` in the demo). Your mail library
(MailKit, JavaMail, smtplib…) speaks the real protocol — EHLO, AUTH, MAIL FROM, DATA. The
only change in your app is configuration: SMTP host/port.

One twist: SMTP has no tenant header, so **the AUTH username names the tenant** — mail sent
as user `acme-pay` lands in acme-pay's inbox. The password is accepted unchecked: this is a
capture tool, the username is addressing, not identity.

## SMS (provider profile)

SMS has no standard protocol — everyone uses the provider's REST API. So Mockifyr ships a
provider **profile**: `--sms-profile twilio` exposes the provider's real endpoint shape:

```
POST /2010-04-01/Accounts/{AccountSid}/Messages.json
```

The official SDK works **unchanged** — point its base URL at Mockifyr. Validation mirrors
the provider (missing `To` → error 21604, missing `From` → 21603, in the provider's own
check order), the response is realistic (`SM…` sid, `status: queued`), and behaviors can
simulate provider errors on demand ("answer 21211 from now on").

## One inbox, and verification as an API

Both channels (and captured broker messages) land in one inbox per tenant:

- `GET /__admin/messages?channel=sms&recipient=…&contains=…&matches=<regex>` — filter, count
- `GET /__admin/messages/otp?recipient=…` — **extracts the OTP code from the message**:

```json
{ "otp": "482913", "messageId": "…", "receivedAt": "…" }
```

That endpoint is the answer to E2E testing's oldest pain: "how do I read the code that was
sent by SMS?" You don't screen-scrape a phone — you call an API. Default pattern finds
4–8 digit codes; a custom regex's first capture group wins.

Housekeeping: the inbox is bounded (`--message-limit`, oldest evicted), behaviors can inject
SMTP faults/delays, and a capture webhook can forward every captured message onwards.
