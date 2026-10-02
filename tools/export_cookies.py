import rookiepy, os

BASE = r"D:\project\Codex\抖音知识库"
LOG = []
try:
    cookies = rookiepy.edge(domains=[".douyin.com", "douyin.com", "www.douyin.com", "iesdouyin.com", "v.douyin.com"])
    LOG.append(f"cookies loaded: {len(cookies)}")

    def netscape_line(c):
        domain = c.get("domain", "")
        include_subdomains = bool(c.get("includeSubdomains", True)) or domain.startswith(".")
        if include_subdomains and not domain.startswith("."):
            domain = "." + domain
        secure = "TRUE" if c.get("secure") else "FALSE"
        http_only = "TRUE" if c.get("httpOnly") else "FALSE"
        expires = int(c.get("expires", 0) or 0)
        return f"{domain}\t{str(include_subdomains).upper()}\t{c.get('path','/')}\t{secure}\t{expires}\t{c.get('name')}\t{c.get('value')}"

    lines = ["# Netscape HTTP Cookie File"]
    seen = set()
    for c in cookies:
        if not c.get("name") or c.get("value") is None:
            continue
        key = (c.get("domain"), c.get("name"))
        if key in seen:
            continue
        seen.add(key)
        lines.append(netscape_line(c))

    out_path = os.path.join(BASE, "data", "cookies.txt")
    with open(out_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    LOG.append(f"written {len(lines)-1} cookies")
except Exception as e:
    LOG.append(f"ERROR: {type(e).__name__}: {e}")

with open(os.path.join(BASE, "data", "cookie_export.log"), "w", encoding="utf-8") as f:
    f.write("\n".join(LOG))