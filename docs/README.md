# Docs

Start here:

- [reference](reference.md): what Linkbase contains, feature by feature, in the style of a manual: synopsis, description, limits, what isn't built.
- [requirements](requirements.md): the same ground as formal, numbered, testable requirements, for tracing a change or a test back to something agreed.
- The **user guide** is in the app, under Guide in the rail (`/guide`), written for the people using it.

Current:

- [architecture](architecture.md): where things live, and where access control would go.
- [models](models.md): what a model is, model files, configuring and switching models, climbing in the generic store.
- [operations](operations.md): backing up, restoring and rebuilding the database, secrets.
- [postgres](postgres.md): getting into the databases and querying them by hand.
- [performance](performance.md): the large Swiss rail sample and how queries are checked against it.
- [extensibility-plan](history/extensibility-plan.md): what's built and what to do next, in order.
- [security-roadmap](security-roadmap.md): sign-in, roles and field-level permissions (not started).
- [audit-history-plan](history/audit-history-plan.md): the audit trail, with a reason for each change (built, apart from reverting changes that touched several records).
- [relationship-attributes](history/relationship-attributes.md): values on links, such as a partner's role (built, with filtering and updating a link's value on CSV re-import; Read text still skips it).
- [deployment-plan](history/deployment-plan.md) and [`deploy/`](../deploy/README.md): running the one image on Kubernetes, OpenShift or Docker.

Finished, kept for the record: [history/](history/).
