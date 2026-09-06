# Security policy

## Supported versions

Until the first stable release, only the latest commit on the default branch is
supported with security fixes.

## Reporting a vulnerability

Use the repository host's private vulnerability-reporting feature. Do not open
a public issue for an unpatched vulnerability and do not include production
tokens, PLC addresses, packet captures, customer data, or personal information.

Include the affected component, impact, minimal reproduction, and any suggested
mitigation. Maintainers will acknowledge the report when available; no response
time guarantee is currently offered.

## Deployment boundary

TapirSLMP does not make SLMP safe to expose publicly. Operators remain
responsible for TLS, host hardening, network segmentation, firewall policy, PLC
access control, token rotation, monitoring, backups, and validating commands
before enabling state-changing operations.
