# Maintainer-provided donation images

The application optionally embeds `wechat.png` and `alipay.png` from this directory. They are intentionally not committed to Git. Builds without these files show that no donation code is configured; they never substitute another recipient's code.

The release workflow reconstructs exact PNG files from base64 repository-secret chunks `DONATION_WECHAT_PNG_1` through `_6` and `DONATION_ALIPAY_PNG_1` through `_6`. Each chunk is at most 40,000 characters; unused chunks are absent. This avoids GitHub's per-secret size limit and Windows environment-value limits. Images must fit in six chunks (about 180 KB). Fork owners should provide their own images or leave the optional resources absent. Donation support does not unlock or restrict any feature.
