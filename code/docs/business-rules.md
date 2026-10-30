# Business rules (legacy operating notes)

> This document is intentionally incomplete. In the original type of system, operators knew exceptions that were implemented only in code.

- Do not exceed customer credit limit without manual validation.
- Customers with serious overdue debt should be blocked.
- High-value orders should be reviewed.
- Risk score is recalculated by the night process.
- Manual commercial override can allow exceptional operations.
- Long-standing customers may receive favorable treatment.

## Known mismatch with code

The exact thresholds, the order in which checks run, country penalties, urgency behavior, card behavior, recent-order penalty, the special handling of very old customers and the exact meaning of manual override are not documented here. These must be recovered from code.
