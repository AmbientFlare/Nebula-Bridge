from __future__ import annotations

import re
from typing import Any


SUPPORTED_FILTERS = {
    "andmatch",
    "append",
    "dateparse",
    "fuzzytime",
    "prepend",
    "querystring",
    "re_replace",
    "regexp",
    "replace",
    "split",
    "timeago",
    "tolower",
    "trim",
    "urldecode",
    "validfilename",
}
SUPPORTED_TEMPLATE_FUNCTIONS = {"and", "eq", "join", "ne", "not", "or", "re_replace"}
TEMPLATE_EXPRESSION = re.compile(r"{{\s*([^{}]+?)\s*}}")


def walk(value: Any):
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def classify(document: dict[str, Any]) -> dict[str, Any]:
    authentication: list[str] = []
    configuration: list[str] = []
    unsupported: list[str] = []

    if document.get("type") != "public":
        authentication.append("Only public indexers are supported by the current client")
    if document.get("login") is not None:
        authentication.append("Cardigann login flow required")
    if document.get("followredirect") is not None:
        unsupported.append("Definition-specific redirect behavior is unsupported")
    for certificate in document.get("certificates") or []:
        normalized_certificate = str(certificate).replace(":", "").strip()
        if not re.fullmatch(r"[A-Fa-f0-9]{40}", normalized_certificate):
            unsupported.append(
                "Certificate fingerprints must be 40-character SHA-1 hex values"
            )
    download = document.get("download")
    if isinstance(download, dict):
        if download.get("before") is not None:
            unsupported.append("Download pre-request flows are unsupported")
        if download.get("selectors") is not None:
            unsupported.append("Download link selector flows are unsupported")
        info_hash = download.get("infohash")
        if not isinstance(info_hash, dict):
            unsupported.append("Only info-hash download flows are currently supported")
        elif info_hash.get("usebeforeresponse") is True:
            unsupported.append(
                "Info-hash download flows using a pre-request response are unsupported"
            )
        method = download.get("method")
        if isinstance(method, str) and method.casefold() != "get":
            unsupported.append(f"Unsupported download HTTP method: {method}")

    for setting in document.get("settings") or []:
        if not isinstance(setting, dict):
            continue
        setting_type = str(setting.get("type") or "")
        setting_name = str(setting.get("name") or "unnamed")
        if setting_type in ("password", "info_cookie"):
            authentication.append(f"Authentication setting required: {setting_name}")
        elif setting_type == "info_flaresolverr":
            unsupported.append("FlareSolverr is unsupported")
        elif setting_type == "select" and "default" not in setting:
            configuration.append(f"Client configuration required: {setting_name}")

    search = document.get("search")
    if not isinstance(search, dict):
        unsupported.append("Search configuration is missing")
        search = {}
    if search.get("preprocessingfilters") is not None:
        unsupported.append("Search preprocessing filters are unsupported")
    if search.get("error") is not None:
        unsupported.append("Definition-specific search error matching is unsupported")

    for path in search.get("paths") or []:
        if not isinstance(path, dict):
            continue
        response = path.get("response") or {}
        response_type = response.get("type", "html") if isinstance(response, dict) else "html"
        if response_type not in ("html", "json", "xml"):
            unsupported.append(f"Unsupported response type: {response_type}")
        method = path.get("method")
        if isinstance(method, str) and "{{" not in method and method not in ("get", "post"):
            unsupported.append(f"Unsupported HTTP method: {method}")
        if path.get("queryseparator") is not None:
            unsupported.append("Custom query separators are unsupported")
        if path.get("followredirect") is not None:
            unsupported.append("Path-specific redirect behavior is unsupported")
        if path.get("categories") is not None:
            unsupported.append("Category-specific search paths are unsupported")
        if path.get("inheritinputs") is False:
            unsupported.append("Disabling inherited search inputs is unsupported")

    for value in walk(document):
        if isinstance(value, dict):
            filters = value.get("filters")
            if isinstance(filters, list):
                for item in filters:
                    if not isinstance(item, dict):
                        continue
                    name = str(item.get("name") or "")
                    if name and name.casefold() not in SUPPORTED_FILTERS:
                        unsupported.append(f"Unsupported Cardigann filter: {name}")
        elif isinstance(value, str) and "{{" in value:
            for match in TEMPLATE_EXPRESSION.finditer(value):
                expression = match.group(1).strip()
                if expression.startswith("if "):
                    expression = expression[3:].lstrip()
                elif expression.startswith("range ") or expression in ("else", "end"):
                    continue
                parts = expression.split(maxsplit=1)
                first = parts[0] if parts else ""
                if (
                    len(parts) > 1
                    and not first.startswith((".", '"'))
                    and first not in SUPPORTED_TEMPLATE_FUNCTIONS
                ):
                    unsupported.append(f"Unsupported Cardigann template function: {first}")

    authentication = sorted(set(authentication), key=str.casefold)
    configuration = sorted(set(configuration), key=str.casefold)
    unsupported = sorted(set(unsupported), key=str.casefold)
    if authentication:
        status = "requires_authentication"
    elif configuration:
        status = "requires_configuration"
    elif unsupported:
        status = "unsupported"
    else:
        status = "compatible"
    notes = authentication + configuration + unsupported
    return {
        "compatibilityStatus": status,
        "compatible": status == "compatible",
        "requiresAuthentication": bool(authentication),
        "requiresConfiguration": bool(configuration),
        "compatibilityNotes": notes,
    }
