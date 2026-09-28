# Security policy

Do not open a public Issue containing credentials, tokens, cookies, private keys, private URLs, or unredacted sensitive logs. Contact the repository owner privately and rotate any exposed credential immediately.

Store secrets in GitHub Secrets, protected Environments, or approved connection storage. Workflows must use least privilege and must not expose secrets to untrusted pull requests.

For a vulnerability, provide the affected path, impact, safe reproduction, and containment recommendation without including secret values. Hardware diagnostic tools must stay non-destructive by default.
