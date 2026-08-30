# Notes

These are just some thoughts I had whilst considering this project.

1. Exact matches vs meaning depends on what the search was. Things that look like identifiers (e.g. "SAS13524") should prioritizing exact match / slightly fuzzy search- whereas words like "travel" needs to consider embeddings more carefully to find emails that relate to travel, and not just contain that exact word.

2. Images could be processed using OCR technology to aid in finding emails where users have pasted images or similar.

3. This email client should probably expose MCP resources so that the emails that are locally stored can be searched by agents such as claude.

4. Mail clients today are mostly built on web based UIs like react- which is good for cross-platform compatibility- but terrible for RAM usage. Since we will use a lot of resources for the data processing, we should probably pick a slimmer UI.

