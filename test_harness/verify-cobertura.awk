function attribute(line, name, marker, start, remainder, finish) {
    marker = name "=\""
    start = index(line, marker)
    if (start == 0) {
        return ""
    }

    remainder = substr(line, start + length(marker))
    finish = index(remainder, "\"")
    return finish == 0 ? "" : substr(remainder, 1, finish - 1)
}

function require_rate(scope, metric, actual, minimum) {
    if (actual == "" || actual + 0 < minimum) {
        printf "Coverage gate failed: %s %s=%s, required >= %.4f\n", scope, metric, actual, minimum > "/dev/stderr"
        failed = 1
    }
}

/<coverage / {
    overall_seen = 1
    overall_line = attribute($0, "line-rate")
    overall_branch = attribute($0, "branch-rate")
}

/<package / {
    package_name = attribute($0, "name")
    package_line[package_name] = attribute($0, "line-rate")
    package_branch[package_name] = attribute($0, "branch-rate")
}

/<class / {
    class_file = attribute($0, "filename")
    if (class_file == "ClipShare.Windows.Infrastructure/EndpointPolicy.cs") {
        endpoint_policy_seen = 1
        endpoint_policy_line = attribute($0, "line-rate")
        endpoint_policy_branch = attribute($0, "branch-rate")
    }
}

END {
    if (!overall_seen) {
        print "Coverage gate failed: Cobertura root element not found." > "/dev/stderr"
        exit 1
    }

    require_rate("overall", "line", overall_line, 0.90)
    require_rate("overall", "branch", overall_branch, 0.85)
    require_rate("Application", "line", package_line["ClipShare.Windows.Application"], 0.90)
    require_rate("Application", "branch", package_branch["ClipShare.Windows.Application"], 0.85)
    require_rate("Infrastructure", "line", package_line["ClipShare.Windows.Infrastructure"], 0.90)
    require_rate("Infrastructure", "branch", package_branch["ClipShare.Windows.Infrastructure"], 0.85)
    require_rate("Platform", "line", package_line["ClipShare.Windows.Platform"], 0.80)
    if (!endpoint_policy_seen) {
        print "Coverage gate failed: EndpointPolicy critical URL guard class not found." > "/dev/stderr"
        failed = 1
    }
    require_rate("EndpointPolicy critical URL guard", "line", endpoint_policy_line, 1.00)
    require_rate("EndpointPolicy critical URL guard", "branch", endpoint_policy_branch, 1.00)

    if (failed) {
        exit 1
    }

    printf "Coverage gate passed: overall line %.2f%%, branch %.2f%%; Application %.2f%%/%.2f%%; Infrastructure %.2f%%/%.2f%%; Platform line %.2f%%; EndpointPolicy %.2f%%/%.2f%%.\n", overall_line * 100, overall_branch * 100, package_line["ClipShare.Windows.Application"] * 100, package_branch["ClipShare.Windows.Application"] * 100, package_line["ClipShare.Windows.Infrastructure"] * 100, package_branch["ClipShare.Windows.Infrastructure"] * 100, package_line["ClipShare.Windows.Platform"] * 100, endpoint_policy_line * 100, endpoint_policy_branch * 100
}
