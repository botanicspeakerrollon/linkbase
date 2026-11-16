# Docs

Start here:

- [reference](reference.md): what Linkbase contains, feature by feature, in the style of a manual: synopsis, description, limits, what isn't built.
- [requirements](requirements.md): the same ground as formal, numbered, testable requirements, for tracing a change or a test back to something agreed. It describes the app as built and is updated with every change; `RequirementsDocTests` checks its structure and its page list.
- The **user guide** is in the app, under Guide in the rail (`/guide`), written for the people using it.

Current:

- [architecture](architecture.md): where things live, how a request and a change reach the data, the read API and change events, and where access control lives.
- [models](models.md): what a model is, model files, configuring and switching models, climbing in the generic store.
- [operations](operations.md): backing up, restoring and rebuilding the database, secrets.
- [postgres](postgres.md): getting into the databases and querying them by hand.
- [schema](schema.md): the tables, views and conventions.
- [performance](performance.md): the large Swiss rail sample and how queries are checked against it.
- [deploy/](../deploy/README.md): running the image on Kubernetes, OpenShift or Docker.

Plans and designs from building the app are kept in [history/](history/). They are historical: most are built, so read them for why, and the pages above for what is true now.
