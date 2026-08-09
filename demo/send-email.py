#!/usr/bin/env python3
"""Sends the demo receipt email through Mockifyr's SMTP capture listener.

The SMTP AUTH username names the tenant — that's how mail lands in acme-pay's inbox.
Multipart/alternative: plain text + a styled HTML receipt.
"""
import os
import smtplib
from email.message import EmailMessage

TEXT = """\
Hello,

Payment PAY-1001 (EUR 149.50) has settled successfully.
Your confirmation code is 738201.

— Acme Payments (sandbox)
"""

HTML = """\
<!DOCTYPE html>
<html>
<body style="margin:0;padding:0;background:#f4f6fb;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
  <div style="max-width:560px;margin:0 auto;padding:32px 16px;">
    <div style="text-align:center;padding-bottom:20px;">
      <span style="display:inline-block;background:#1d4ed8;color:#ffffff;font-size:14px;font-weight:700;
                   letter-spacing:2px;border-radius:8px;padding:8px 16px;">ACME&nbsp;PAYMENTS</span>
    </div>
    <div style="background:#ffffff;border:1px solid #e5e9f2;border-radius:16px;overflow:hidden;
                box-shadow:0 4px 16px rgba(16,24,40,.06);">
      <div style="background:#0f172a;padding:28px 32px;">
        <p style="margin:0;color:#94a3b8;font-size:13px;letter-spacing:1px;">PAYMENT SETTLED</p>
        <p style="margin:6px 0 0;color:#ffffff;font-size:34px;font-weight:700;">&euro;149.50</p>
      </div>
      <div style="padding:28px 32px;">
        <p style="margin:0 0 20px;color:#334155;font-size:15px;line-height:1.6;">
          Hello,<br>your payment has settled successfully. Details below.
        </p>
        <table style="width:100%;border-collapse:collapse;font-size:14px;color:#334155;">
          <tr>
            <td style="padding:10px 0;color:#64748b;border-bottom:1px solid #eef1f6;">Payment ID</td>
            <td style="padding:10px 0;text-align:right;font-weight:600;border-bottom:1px solid #eef1f6;">PAY-1001</td>
          </tr>
          <tr>
            <td style="padding:10px 0;color:#64748b;border-bottom:1px solid #eef1f6;">Status</td>
            <td style="padding:10px 0;text-align:right;border-bottom:1px solid #eef1f6;">
              <span style="background:#dcfce7;color:#15803d;font-weight:600;border-radius:999px;
                           padding:3px 12px;font-size:13px;">settled</span>
            </td>
          </tr>
          <tr>
            <td style="padding:10px 0;color:#64748b;border-bottom:1px solid #eef1f6;">Currency</td>
            <td style="padding:10px 0;text-align:right;font-weight:600;border-bottom:1px solid #eef1f6;">EUR</td>
          </tr>
          <tr>
            <td style="padding:10px 0;color:#64748b;">Date</td>
            <td style="padding:10px 0;text-align:right;font-weight:600;">06 Aug 2026</td>
          </tr>
        </table>
        <div style="margin-top:24px;background:#f8fafc;border:1px dashed #cbd5e1;border-radius:12px;
                    padding:18px;text-align:center;">
          <p style="margin:0 0 6px;color:#64748b;font-size:13px;">Confirmation code</p>
          <p style="margin:0;color:#0f172a;font-size:28px;font-weight:700;letter-spacing:6px;">738201</p>
        </div>
      </div>
    </div>
    <p style="text-align:center;color:#94a3b8;font-size:12px;margin-top:20px;">
      Acme Payments sandbox &middot; no real money moved &middot; captured by Mockifyr
    </p>
  </div>
</body>
</html>
"""

msg = EmailMessage()
msg["From"] = "noreply@acme-payments.example"
msg["To"] = "dev@merchant.example"
msg["Subject"] = "Your payment PAY-1001 has settled"
msg.set_content(TEXT)
msg.add_alternative(HTML, subtype="html")

with smtplib.SMTP(os.environ.get("MOCKIFYR_SMTP_HOST", "localhost"),
                  int(os.environ.get("MOCKIFYR_SMTP_PORT", "2525")), timeout=10) as smtp:
    smtp.login("acme-pay", "anything")  # username = tenant
    smtp.send_message(msg)

print("sent: 'Your payment PAY-1001 has settled' -> dev@merchant.example (tenant acme-pay)")
