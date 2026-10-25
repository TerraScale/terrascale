Continuous backup discovers tenant files on disk, but opens a tap only after the
current, verified management catalog confirms ownership. The ownership rule is
shared with the management file audit: a project database record or a backup
snapshot still tracked by that organization and project. Unknown or unreadable
catalog state never authorizes shipping a tenant file. Existing taps stop shipping
when ownership is lost; discovery also releases them.

The system database and intents journal retain their existing backup behavior.
Registered databases retain snapshot and change-log shipping and point-in-time
restore. This changes no public API or storage format.
