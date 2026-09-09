#!/bin/sh

set -eu

: "${CLIPSHARE_API_LEVELS:=29 31 33 34 35 36}"
: "${CLIPSHARE_ANDROID_PROJECT:=/work/current/clients/android}"
: "${CLIPSHARE_MATRIX_OUTPUT:=/work/output}"
: "${CLIPSHARE_GRADLE_HOME:=/opt/clipshare-a1-gradle-cache}"
: "${CLIPSHARE_PLATFORM_COVERAGE_INIT:=/work/harness/android-c2-platform-coverage.init.gradle}"

sdk_root=${ANDROID_SDK_ROOT:-/opt/android-sdk}
emulator_root="$sdk_root/emulator"
adb="$sdk_root/platform-tools/adb"
avdmanager="$sdk_root/cmdline-tools/latest/bin/avdmanager"
export ANDROID_AVD_HOME=/work/runtime/avd
export ANDROID_USER_HOME=/work/runtime/android-user
export LD_LIBRARY_PATH="$emulator_root/lib64:$emulator_root/lib64/qt/lib:$emulator_root/lib64/gles_swiftshader"

emulator_pid=""
emulator_serial=""
api_output=""

cleanup_emulator() {
    if [ -n "$api_output" ] && [ -n "$emulator_serial" ]; then
        timeout 15 "$adb" -s "$emulator_serial" logcat -d -t 500 -v threadtime \
            > "$api_output/logcat-tail.txt" 2>&1 || true
        timeout 10 "$adb" -s "$emulator_serial" emu kill >/dev/null 2>&1 || true
    fi
    if [ -n "$emulator_pid" ]; then
        wait_count=0
        while kill -0 "$emulator_pid" 2>/dev/null && [ "$wait_count" -lt 30 ]; do
            process_state=$(ps -o stat= -p "$emulator_pid" 2>/dev/null | tr -d ' ' || true)
            case "$process_state" in
                ""|Z*) break ;;
            esac
            sleep 1
            wait_count=$((wait_count + 1))
        done
        if kill -0 "$emulator_pid" 2>/dev/null; then
            kill -TERM "$emulator_pid" 2>/dev/null || true
            sleep 2
        fi
        if kill -0 "$emulator_pid" 2>/dev/null; then
            kill -KILL "$emulator_pid" 2>/dev/null || true
        fi
        wait "$emulator_pid" 2>/dev/null || true
    fi
    emulator_pid=""
    emulator_serial=""
}

trap cleanup_emulator EXIT HUP INT TERM

test -x "$emulator_root/emulator"
test -x "$adb"
test -x "$avdmanager"
test -f "$CLIPSHARE_ANDROID_PROJECT/settings.gradle.kts"
test -f "$CLIPSHARE_PLATFORM_COVERAGE_INIT"
test "$(find "$sdk_root/system-images" -mindepth 4 -maxdepth 4 \
    -type f -name source.properties | wc -l)" -eq 6
test "$(find "$sdk_root/system-images" -mindepth 4 -maxdepth 4 \
    -type f -name package.xml | wc -l)" -eq 0

mkdir -p "$ANDROID_AVD_HOME" "$ANDROID_USER_HOME" "$CLIPSHARE_MATRIX_OUTPUT"
"$avdmanager" list target > "$CLIPSHARE_MATRIX_OUTPUT/avdmanager-targets.txt" 2>&1

for api_level in $CLIPSHARE_API_LEVELS; do
    case "$api_level" in
        29|31|33|34|35|36) ;;
        *) echo "Unsupported C2 matrix API level: $api_level" >&2; exit 64 ;;
    esac

    avd_name="clipshareC2Api${api_level}"
    emulator_serial="emulator-5554"
    api_output="$CLIPSHARE_MATRIX_OUTPUT/api-${api_level}"
    mkdir -p "$api_output/instrumentation-results" \
        "$api_output/instrumentation-report" "$api_output/platform-coverage"
    find "$ANDROID_AVD_HOME" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +

    printf 'no\n' | "$avdmanager" create avd \
        --force \
        --name "$avd_name" \
        --path "$ANDROID_AVD_HOME/${avd_name}.avd" \
        --package "system-images;android-${api_level};default;x86_64" \
        --device pixel_2 \
        --abi x86_64 \
        --tag default \
        > "$api_output/avd-create.log" 2>&1

    "$adb" kill-server >/dev/null 2>&1 || true
    "$emulator_root/emulator" \
        -avd "$avd_name" \
        -port 5554 \
        -no-window \
        -no-audio \
        -no-boot-anim \
        -no-snapshot \
        -wipe-data \
        -no-metrics \
        -camera-back none \
        -camera-front none \
        -gpu swiftshader_indirect \
        -accel off \
        -cores 2 \
        -memory 1536 \
        > "$api_output/emulator.log" 2>&1 &
    emulator_pid=$!
    printf '%s\n' "$emulator_pid" > "$api_output/emulator.pid"

    boot_elapsed=0
    boot_completed=""
    while [ "$boot_elapsed" -lt 900 ]; do
        process_state=$(ps -o stat= -p "$emulator_pid" 2>/dev/null | tr -d ' ' || true)
        case "$process_state" in
            ""|Z*) echo "API ${api_level} emulator exited before boot completed." >&2; exit 1 ;;
        esac
        adb_state=$(timeout 5 "$adb" -s "$emulator_serial" get-state 2>/dev/null || true)
        if [ "$adb_state" = device ]; then
            boot_completed=$(timeout 5 "$adb" -s "$emulator_serial" \
                shell getprop sys.boot_completed 2>/dev/null | tr -d '\r' || true)
            if [ "$boot_completed" = 1 ]; then
                break
            fi
        fi
        sleep 2
        boot_elapsed=$((boot_elapsed + 2))
    done
    if [ "$boot_completed" != 1 ]; then
        echo "API ${api_level} emulator did not boot within 900 seconds." >&2
        exit 1
    fi

    {
        echo "api_level=$api_level"
        echo "serial=$emulator_serial"
        echo "adb_state=$($adb -s "$emulator_serial" get-state)"
        echo "sys.boot_completed=$($adb -s "$emulator_serial" shell getprop sys.boot_completed | tr -d '\r')"
        echo "sdk=$($adb -s "$emulator_serial" shell getprop ro.build.version.sdk | tr -d '\r')"
        echo "abi=$($adb -s "$emulator_serial" shell getprop ro.product.cpu.abi | tr -d '\r')"
        echo "boot_elapsed_seconds=$boot_elapsed"
    } > "$api_output/boot-properties.txt"
    test "$(sed -n 's/^sdk=//p' "$api_output/boot-properties.txt")" = "$api_level"

    timeout 30 "$adb" -s "$emulator_serial" shell settings put global window_animation_scale 0
    timeout 30 "$adb" -s "$emulator_serial" shell settings put global transition_animation_scale 0
    timeout 30 "$adb" -s "$emulator_serial" shell settings put global animator_duration_scale 0
    timeout 30 "$adb" -s "$emulator_serial" shell svc power stayon true
    timeout 30 "$adb" -s "$emulator_serial" shell input keyevent KEYCODE_WAKEUP
    timeout 30 "$adb" -s "$emulator_serial" shell wm dismiss-keyguard || true
    timeout 30 "$adb" -s "$emulator_serial" shell locksettings set-disabled true \
        > "$api_output/locksettings-disable.txt" 2>&1 || true

    result_root="$CLIPSHARE_ANDROID_PROJECT/platform/android/build/outputs/androidTest-results/connected/debug"
    report_root="$CLIPSHARE_ANDROID_PROJECT/platform/android/build/reports/androidTests/connected/debug"
    coverage_root="$CLIPSHARE_ANDROID_PROJECT/build/reports/jacoco/c2PlatformCoverage"
    rm -rf "$CLIPSHARE_ANDROID_PROJECT/platform/android/build/outputs/androidTest-results/connected" \
        "$CLIPSHARE_ANDROID_PROJECT/platform/android/build/reports/androidTests/connected" \
        "$CLIPSHARE_ANDROID_PROJECT/platform/android/build/outputs/code_coverage" \
        "$coverage_root"

    gradle_status=0
    (
        cd "$CLIPSHARE_ANDROID_PROJECT"
        gradle \
            '-Dorg.gradle.jvmargs=-Xmx2048m -XX:MaxMetaspaceSize=768m' \
            --gradle-user-home "$CLIPSHARE_GRADLE_HOME" \
            --offline \
            --no-daemon \
            --max-workers=2 \
            --console=plain \
            --dependency-verification strict \
            --init-script "$CLIPSHARE_PLATFORM_COVERAGE_INIT" \
            :platform:android:connectedDebugAndroidTest || exit $?
        gradle \
            '-Dorg.gradle.jvmargs=-Xmx2048m -XX:MaxMetaspaceSize=768m' \
            --gradle-user-home "$CLIPSHARE_GRADLE_HOME" \
            --offline \
            --no-daemon \
            --max-workers=2 \
            --console=plain \
            --dependency-verification strict \
            --init-script "$CLIPSHARE_PLATFORM_COVERAGE_INIT" \
            c2PlatformCoverageReport c2PlatformCoverageVerification || exit $?
    ) > "$api_output/gradle-connected.log" 2>&1 || gradle_status=$?

    result_count=$(find "$result_root" -maxdepth 1 -type f -name 'TEST-*.xml' 2>/dev/null | wc -l)
    coverage_count=$(find "$coverage_root" -type f -name '*.xml' 2>/dev/null | wc -l)
    find "$result_root" -maxdepth 1 -type f -name 'TEST-*.xml' \
        -exec cp {} "$api_output/instrumentation-results/" \; 2>/dev/null || true
    if [ -d "$report_root" ]; then
        cp -R "$report_root/." "$api_output/instrumentation-report/"
    fi
    find "$coverage_root" -type f -name '*.xml' \
        -exec cp {} "$api_output/platform-coverage/" \; 2>/dev/null || true
    {
        echo "api_level=$api_level"
        echo "gradle_status=$gradle_status"
        echo "instrumentation_xml_count=$result_count"
        echo "platform_coverage_xml_count=$coverage_count"
    } > "$api_output/run-status.txt"
    if [ "$result_count" -ne 1 ]; then
        echo "API ${api_level} produced ${result_count} instrumentation XML reports; expected one." >&2
        exit 1
    fi
    if [ "$gradle_status" -ne 0 ]; then
        echo "API ${api_level} C2 instrumentation/coverage failed with exit code ${gradle_status}." >&2
        exit "$gradle_status"
    fi
    if [ "$coverage_count" -ne 1 ]; then
        echo "API ${api_level} produced ${coverage_count} coverage XML reports; expected one." >&2
        exit 1
    fi

    cleanup_emulator
    api_output=""
done

"$adb" kill-server >/dev/null 2>&1 || true
printf 'completed_api_levels=%s\n' "$CLIPSHARE_API_LEVELS" \
    > "$CLIPSHARE_MATRIX_OUTPUT/matrix-status.txt"
