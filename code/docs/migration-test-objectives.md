# Migration test objectives

This fixture is designed to test whether a migration system can recover behavior rather than merely translate syntax.

Expected capabilities:

1. Discover a Web Forms frontend and code-behind event model.
2. Recover the customer/order model from `DataSet` schema creation rather than ORM entities.
3. Separate persistence, UI, business decision and background processing.
4. Detect that not all business rules live in `CreditDecisionService`.
5. Preserve rule ordering and short-circuit behavior.
6. Preserve the semantic difference between manual override and hard overdue blocking.
7. Identify static in-memory state that is not persisted.
8. Identify concurrency protection around shared XML persistence.
9. Reproduce the periodic batch independently of HTTP requests.
10. Generate regression tests for supplied scenarios before refactoring.
