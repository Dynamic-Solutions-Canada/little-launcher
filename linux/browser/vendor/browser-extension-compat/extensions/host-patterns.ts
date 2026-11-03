interface HostPattern {
  schemes: string[];
  host: string;
  subdomains: boolean;
  path: string;
}

/** Parse Chrome patterns; packaged extension schemes are allowed only for tab URL queries. */
function parsePattern(pattern: string, allowExtensionScheme = false): HostPattern {
  if (pattern === "<all_urls>") {
    return {
      schemes: ["http:", "https:", "file:", "ftp:"],
      host: "*",
      subdomains: true,
      path: "/*",
    };
  }
  const match = /^(\*|https?|file|ftp|chrome-extension):\/\/([^/]*)(\/.*)$/.exec(pattern);
  if (!match) {
    throw new Error(`Invalid extension host pattern: ${pattern}`);
  }
  const scheme = match[1];
  const rawHost = match[2];
  const pathname = match[3];
  if (scheme === undefined || rawHost === undefined || pathname === undefined) {
    throw new Error("Invalid extension host pattern.");
  }
  if (scheme === "chrome-extension" && !allowExtensionScheme) {
    throw new Error("Packaged extension URLs cannot be used as host permission grants.");
  }
  const subdomains = rawHost.startsWith("*.");
  const host = subdomains ? rawHost.slice(2) : rawHost;
  if ((scheme !== "file" && !host) || host.includes(":") || (host !== "*" && host.includes("*"))) {
    throw new Error(`Invalid extension host pattern: ${pattern}`);
  }
  return {
    schemes: scheme === "*" ? ["http:", "https:"] : [`${scheme}:`],
    host: host.toLowerCase(),
    subdomains,
    path: pathname,
  };
}

function hostMatches(pattern: HostPattern, host: string): boolean {
  return (
    pattern.host === "*" ||
    pattern.host === host ||
    (pattern.subdomains && host.endsWith(`.${pattern.host}`))
  );
}

/** Match URL-query patterns with escaped literal characters and Chrome wildcard host semantics. */
export function matchesExtensionUrl(pattern: string, url: string): boolean {
  const parsed = parsePattern(pattern, true);
  const target = new URL(url);
  if (!parsed.schemes.includes(target.protocol) || !hostMatches(parsed, target.hostname)) {
    return false;
  }
  const pieces = parsed.path.split("*");
  const escaped = pieces.map((piece) => piece.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  const pathname = `${target.pathname}${target.search}`;
  return new RegExp(`^${escaped.join(".*")}$`).test(pathname);
}

/** Required host grants cover a page's scheme and host regardless of their path component. */
export function matchesExtensionHostGrant(pattern: string, url: string): boolean {
  const parsed = parsePattern(pattern);
  const target = new URL(url);
  return parsed.schemes.includes(target.protocol) && hostMatches(parsed, target.hostname);
}

/** Required host grants cover narrower origin patterns, including wildcard subdomains and all URLs. */
export function coversExtensionOrigin(granted: string, requested: string): boolean {
  const grant = parsePattern(granted);
  const request = parsePattern(requested);
  if (!request.schemes.every((scheme) => grant.schemes.includes(scheme))) {
    return false;
  }
  if (request.host === "*") {
    return grant.host === "*";
  }
  if (!hostMatches(grant, request.host)) {
    return false;
  }
  return !request.subdomains || grant.host === "*" || grant.subdomains;
}
