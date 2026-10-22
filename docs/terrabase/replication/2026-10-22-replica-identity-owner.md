# Replica identity ownership

The node-local `.terrabase.replica` record uses format 2. Its checksum covers the member, epoch, running/stopped marker and a SHA-256 fingerprint of the configured node identity, host name and absolute data-directory path.

A clean restart with the same owner preserves its member and epoch. An unclean restart increments the epoch. Moving or cloning the data root, or changing the node identity, mints a fresh member instead of inheriting the donor's dots and rebuild certificates. Startup withdraws certificates issued for a different member before serving the copied data.

This binding depends on distinct host or configured node identities when a disk is cloned to the same path on another host. It cannot distinguish two hosts that deliberately reuse every identity input. The fingerprint is an ownership check, not authentication.

Needs format bump. Format-1 identity records are refused; there is no migration or compatibility shim. This change does not alter the replication wire protocol or `StorageFormat.cs`.
