#!/bin/sh

set -eu

: "${CLIPSHARE_API_LEVELS:=29 31 33 34 35 36}"
: "${CLIPSHARE_ANDROID_PROJECT:=/work/current/clients/android}"
: "${CLIPSHARE_MATRIX_OUTPUT:=/work/output}"
: "${CLIPSHARE_GRADLE_HOME:=/work/runtime/gradle-home}"
: "${CLIPSHARE_PLATFORM_COVERAGE_INIT:=/work/harness/android-a1-platform-coverage.init.gradle}"

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
        *) echo "Unsupported A1 matrix API level: $api_level" >&2; exit 64 ;;
    esac

    avd_name="clipshareApi${api_level}"
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
    error_dialogs_disabled=0
    while [ "$boot_elapsed" -lt 900 ]; do
        process_state=$(ps -o stat= -p "$emulator_pid" 2>/dev/null | tr -d ' ' || true)
        case "$process_state" in
            ""|Z*)
                echo "API ${api_level} emulator exited before boot completed." >&2
                exit 1
                ;;
        esac
        adb_state=$(timeout 5 "$adb" -s "$emulator_serial" get-state 2>/dev/null || true)
        if [ "$adb_state" = device ]; then
            if [ "$error_dialogs_disabled" -eq 0 ]; then
                if timeout 5 "$adb" -s "$emulator_serial" shell \
                    settings put global hide_error_dialogs 1 >/dev/null 2>&1; then
                    timeout 5 "$adb" -s "$emulator_serial" shell \
                        settings put global show_first_crash_dialog 0 >/dev/null 2>&1 || true
                    error_dialogs_disabled=1
                fi
            fi
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
        echo "dev.bootcomplete=$($adb -s "$emulator_serial" shell getprop dev.bootcomplete | tr -d '\r')"
        echo "sdk=$($adb -s "$emulator_serial" shell getprop ro.build.version.sdk | tr -d '\r')"
        echo "abi=$($adb -s "$emulator_serial" shell getprop ro.product.cpu.abi | tr -d '\r')"
        echo "boot_elapsed_seconds=$boot_elapsed"
    } > "$api_output/boot-properties.txt"
    test "$(sed -n 's/^sdk=//p' "$api_output/boot-properties.txt")" = "$api_level"

    timeout 30 "$adb" -s "$emulator_serial" shell \
        settings put global window_animation_scale 0
    timeout 30 "$adb" -s "$emulator_serial" shell \
        settings put global transition_animation_scale 0
    timeout 30 "$adb" -s "$emulator_serial" shell \
        settings put global animator_duration_scale 0
    timeout 30 "$adb" -s "$emulator_serial" shell svc power stayon true
    timeout 30 "$adb" -s "$emulator_serial" shell \
        settings put system screen_off_timeout 2147483647
    timeout 30 "$adb" -s "$emulator_serial" shell \
        locksettings set-disabled true > "$api_output/locksettings-disable.txt" 2>&1 || true
    timeout 30 "$adb" -s "$emulator_serial" shell \
        settings put secure lockscreen.disabled 1 >> "$api_output/locksettings-disable.txt" 2>&1 || true

    foreground_attempt=0
    while [ "$foreground_attempt" -lt 30 ]; do
        timeout 30 "$adb" -s "$emulator_serial" shell input keyevent KEYCODE_WAKEUP
        timeout 30 "$adb" -s "$emulator_serial" shell wm dismiss-keyguard
        timeout 30 "$adb" -s "$emulator_serial" shell input keyevent KEYCODE_MENU
        timeout 30 "$adb" -s "$emulator_serial" shell input swipe 540 1600 540 200 300
        timeout 30 "$adb" -s "$emulator_serial" shell input keyevent KEYCODE_HOME
        sleep 2
        foreground_attempt=$((foreground_attempt + 1))
        timeout 30 "$adb" -s "$emulator_serial" shell dumpsys window > \
            "$api_output/window-preparation.txt"
        dreaming_lockscreen=$(sed -n \
            's/.*mDreamingLockscreen=\([^[:space:]]*\).*/\1/p' \
            "$api_output/window-preparation.txt" | tail -n 1 | tr -d '\r')
        current_focus=$(sed -n \
            's/^[[:space:]]*mCurrentFocus=//p' \
            "$api_output/window-preparation.txt" | tail -n 1 | tr -d '\r')
        foreground_focus_ready=false
        case "$current_focus" in
            Window*Keyguard*|Window*StatusBar*|Window*"Application Not Responding:"*) ;;
            Window*) foreground_focus_ready=true ;;
        esac
        if [ "$foreground_focus_ready" = true ] || \
            { [ -n "$dreaming_lockscreen" ] && [ "$dreaming_lockscreen" != true ]; }; then
            break
        fi
    done
    {
        echo "unlock_attempts=$foreground_attempt"
        echo "power_state"
        timeout 30 "$adb" -s "$emulator_serial" shell dumpsys power | \
            grep -E 'mWakefulness=|mHoldingDisplaySuspendBlocker=' || true
        echo "window_state"
        timeout 30 "$adb" -s "$emulator_serial" shell dumpsys window | \
            grep -E 'mCurrentFocus=|mFocusedApp=|mDreamingLockscreen=|mShowingLockscreen=|isStatusBarKeyguard=' || true
    } > "$api_output/foreground-preparation.txt"
    effective_dreaming_lockscreen=$(sed -n \
        's/.*mDreamingLockscreen=\([^[:space:]]*\).*/\1/p' \
        "$api_output/foreground-preparation.txt" | tail -n 1 | tr -d '\r')
    effective_current_focus=$(sed -n \
        's/^[[:space:]]*mCurrentFocus=//p' \
        "$api_output/foreground-preparation.txt" | tail -n 1 | tr -d '\r')
    effective_focus_ready=false
    case "$effective_current_focus" in
        Window*Keyguard*|Window*StatusBar*|Window*"Application Not Responding:"*) ;;
        Window*) effective_focus_ready=true ;;
    esac
    echo "effective_mDreamingLockscreen=$effective_dreaming_lockscreen" >> \
        "$api_output/foreground-preparation.txt"
    echo "effective_mCurrentFocus=$effective_current_focus" >> \
        "$api_output/foreground-preparation.txt"
    echo "effective_focus_ready=$effective_focus_ready" >> \
        "$api_output/foreground-preparation.txt"
    if grep -F 'Application Not Responding:' "$api_output/foreground-preparation.txt"; then
        echo "System ANR dialog retained input focus before API ${api_level} tests." >&2
        exit 1
    fi
    if [ "$effective_dreaming_lockscreen" = true ] && [ "$effective_focus_ready" != true ]; then
        echo "Keyguard remained active before API ${api_level} tests." >&2
        exit 1
    fi

    find "$CLIPSHARE_ANDROID_PROJECT/app/build/outputs/androidTest-results/connected" \
        -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + 2>/dev/null || true
    find "$CLIPSHARE_ANDROID_PROJECT/app/build/reports/androidTests/connected" \
        -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + 2>/dev/null || true
    find "$CLIPSHARE_ANDROID_PROJECT/app/build/reports/coverage" \
        -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + 2>/dev/null || true
    find "$CLIPSHARE_ANDROID_PROJECT/app/build/outputs/code_coverage" \
        -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + 2>/dev/null || true
    rm -rf "$CLIPSHARE_ANDROID_PROJECT/build/reports/jacoco/a1PlatformCoverage"

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
            connectedDebugAndroidTest || exit $?
        gradle \
            '-Dorg.gradle.jvmargs=-Xmx2048m -XX:MaxMetaspaceSize=768m' \
            --gradle-user-home "$CLIPSHARE_GRADLE_HOME" \
            --offline \
            --no-daemon \
            --max-workers=2 \
            --console=plain \
            --dependency-verification strict \
            --init-script "$CLIPSHARE_PLATFORM_COVERAGE_INIT" \
            a1PlatformCoverageReport \
            a1PlatformCoverageVerification
    ) > "$api_output/gradle-connected.log" 2>&1 || gradle_status=$?

    result_root="$CLIPSHARE_ANDROID_PROJECT/app/build/outputs/androidTest-results/connected/debug"
    report_root="$CLIPSHARE_ANDROID_PROJECT/app/build/reports/androidTests/connected/debug"
    coverage_root="$CLIPSHARE_ANDROID_PROJECT/build/reports/jacoco/a1PlatformCoverage"
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
        echo "API ${api_level} produced ${result_count} instrumentation XML reports; expected exactly one." >&2
        exit 1
    fi
    if [ "$gradle_status" -ne 0 ]; then
        echo "API ${api_level} AndroidTest/coverage Gradle tasks failed with exit code ${gradle_status}." >&2
        exit "$gradle_status"
    fi
    if [ "$coverage_count" -ne 1 ]; then
        echo "API ${api_level} produced ${coverage_count} coverage XML reports; expected exactly one." >&2
        exit 1
    fi

    if [ "$api_level" = 29 ]; then
        app_apk="$CLIPSHARE_ANDROID_PROJECT/app/build/outputs/apk/debug/app-debug.apk"
        test -f "$app_apk"
        {
            echo "api_level=$api_level"
            install_output=$($adb -s "$emulator_serial" install -r "$app_apk")
            printf 'install_output=%s\n' "$install_output"
            printf '%s\n' "$install_output" | grep -F "Success"

            launch_output=$($adb -s "$emulator_serial" shell am start \
                -n com.clipshare.android/.MainActivity | tr -d '\r')
            printf '%s\n' "$launch_output"
            printf '%s\n' "$launch_output" | grep -F "Starting: Intent"

            launch_elapsed=0
            process_id=""
            resumed_activity=""
            while [ "$launch_elapsed" -lt 60 ]; do
                process_id=$($adb -s "$emulator_serial" shell pidof \
                    com.clipshare.android 2>/dev/null | tr -d '\r' || true)
                resumed_activity=$($adb -s "$emulator_serial" shell dumpsys activity activities \
                    2>/dev/null | tr -d '\r' | \
                    grep -E 'mResumedActivity|topResumedActivity' | \
                    grep -F 'com.clipshare.android/.MainActivity' | head -1 || true)
                if [ -n "$process_id" ] && [ -n "$resumed_activity" ]; then
                    break
                fi
                sleep 2
                launch_elapsed=$((launch_elapsed + 2))
            done
            test -n "$process_id"
            test -n "$resumed_activity"
            printf 'process_id=%s\n' "$process_id"
            printf 'resumed_activity=%s\n' "$resumed_activity"
            printf 'launch_elapsed_seconds=%s\n' "$launch_elapsed"
            echo "activity_launch=passed"

            package_before=$($adb -s "$emulator_serial" shell pm path \
                com.clipshare.android | tr -d '\r')
            printf 'package_before_uninstall=%s\n' "$package_before"
            printf '%s\n' "$package_before" | grep -F "package:"

            uninstall_output=$($adb -s "$emulator_serial" uninstall com.clipshare.android)
            printf 'uninstall_output=%s\n' "$uninstall_output"
            printf '%s\n' "$uninstall_output" | grep -F "Success"

            package_after=$($adb -s "$emulator_serial" shell pm path \
                com.clipshare.android | tr -d '\r')
            printf 'package_after_uninstall=%s\n' "$package_after"
            test -z "$package_after"
            echo "package_lifecycle=passed"
        } > "$api_output/package-lifecycle.log" 2>&1
    fi

    cleanup_emulator
    api_output=""
done

"$adb" kill-server >/dev/null 2>&1 || true
printf 'completed_api_levels=%s\n' "$CLIPSHARE_API_LEVELS" \
    > "$CLIPSHARE_MATRIX_OUTPUT/matrix-status.txt"
