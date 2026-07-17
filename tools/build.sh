#!/usr/bin/env bash
set -euo pipefail

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
readonly REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
readonly APP_PROJECT_REL="src/CS2FocusGuard.App/CS2FocusGuard.App.csproj"
readonly INSTALLER_SCRIPT_REL="installer/CS2FocusGuard.iss"
readonly APP_PROJECT="$REPO_ROOT/$APP_PROJECT_REL"
readonly INSTALLER_SCRIPT="$REPO_ROOT/$INSTALLER_SCRIPT_REL"
readonly DEBUG_EXE="$REPO_ROOT/src/CS2FocusGuard.App/bin/Debug/net8.0-windows/CS2FocusGuard.exe"
readonly PUBLISH_DIR="$REPO_ROOT/publish"
readonly ARTIFACT_DIR="$REPO_ROOT/artifacts"
readonly RELEASE_REMOTE="origin"
readonly RELEASE_BRANCH="main"
readonly GITHUB_REPOSITORY_SLUG="League2EB/CS2FocusGuard"
release_backup_dir=""
release_versions_modified=false
release_version_committed=false
release_version_override=""

fail() {
    printf '錯誤: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "找不到必要指令: $1"
}

wait_for_app_exit() {
    powershell.exe -NoProfile -NonInteractive -Command '
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $processes = Get-Process -Name "CS2FocusGuard" -ErrorAction SilentlyContinue
            if ($null -eq $processes) {
                exit 0
            }

            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)

        Write-Error "CS2 Focus Guard 未在 15 秒內結束。"
        exit 1
    '
}

close_running_app() {
    local local_app_data installed_exe install_directory exit_command_exe=""

    if [[ -f "$DEBUG_EXE" ]]; then
        exit_command_exe="$DEBUG_EXE"
    elif [[ -n "${LOCALAPPDATA:-}" ]]; then
        local_app_data="$(cygpath -u "$LOCALAPPDATA")"
        for install_directory in "$local_app_data/Programs/CS2 Focus Guard"; do
            installed_exe="$install_directory/CS2FocusGuard.exe"
            if [[ -f "$installed_exe" ]]; then
                exit_command_exe="$installed_exe"
                break
            fi
        done
    fi

    if [[ -n "$exit_command_exe" ]]; then
        printf '正在結束已執行的應用程式...\n'
        "$exit_command_exe" --exit ||
            fail "無法通知既有的 CS2 Focus Guard 結束。"
        wait_for_app_exit ||
            fail "既有的 CS2 Focus Guard 尚未完全結束。"
    fi
}

launch_debug_app() {
    local debug_exe_windows local_app_data startup_log=""

    debug_exe_windows="$(cygpath -w "$DEBUG_EXE")"
    if [[ -n "${LOCALAPPDATA:-}" ]]; then
        local_app_data="$(cygpath -u "$LOCALAPPDATA")"
        startup_log="$local_app_data/CS2FocusGuard/startup-error.log"
        startup_log="$(cygpath -w "$startup_log")"
    fi

    CS2FG_DEBUG_EXE="$debug_exe_windows" \
        CS2FG_STARTUP_LOG="$startup_log" \
        powershell.exe -NoProfile -NonInteractive -Command '
            $exe = $env:CS2FG_DEBUG_EXE
            $startupLog = $env:CS2FG_STARTUP_LOG
            $process = Start-Process -FilePath $exe -PassThru
            $deadline = [DateTime]::UtcNow.AddSeconds(15)

            do {
                Start-Sleep -Milliseconds 200
                $process.Refresh()

                if ($process.HasExited) {
                    Write-Error "Debug 應用程式在顯示主視窗前結束，ExitCode=$($process.ExitCode)。"
                    if ($startupLog -and (Test-Path -LiteralPath $startupLog)) {
                        Get-Content -LiteralPath $startupLog -Tail 40
                    }

                    exit 1
                }

                if ($process.MainWindowHandle -ne 0) {
                    Write-Output "Debug 主視窗已啟動，PID=$($process.Id)。"
                    exit 0
                }
            } while ([DateTime]::UtcNow -lt $deadline)

            Write-Error "Debug 應用程式已執行，但 15 秒內沒有建立主視窗。"
            if ($startupLog -and (Test-Path -LiteralPath $startupLog)) {
                Get-Content -LiteralPath $startupLog -Tail 40
            }

            Start-Process -FilePath $exe -ArgumentList "--exit" -Wait
            if (-not $process.WaitForExit(15000)) {
                Stop-Process -Id $process.Id -Force
            }

            exit 1
        '
}

run_local_build() {
    require_command dotnet
    require_command cygpath
    require_command powershell.exe

    close_running_app

    printf '正在編譯 Debug 版本...\n'
    dotnet build "$APP_PROJECT" -c Debug --nologo

    [[ -f "$DEBUG_EXE" ]] || fail "找不到編譯輸出: $DEBUG_EXE"

    printf '正在啟動 Debug 版本...\n'
    launch_debug_app
}

ensure_version_files_clean() {
    require_command git

    if ! git -C "$REPO_ROOT" diff --quiet -- "$APP_PROJECT_REL" "$INSTALLER_SCRIPT_REL" ||
        ! git -C "$REPO_ROOT" diff --cached --quiet -- "$APP_PROJECT_REL" "$INSTALLER_SCRIPT_REL"; then
        fail "版本檔案已有未提交變更，請先確認或提交後再打包。"
    fi
}

ensure_release_worktree_clean() {
    if [[ -n "$(git -C "$REPO_ROOT" status --porcelain --untracked-files=all)" ]]; then
        fail "正式打包前工作目錄必須完全乾淨，請先確認或提交所有變更。"
    fi
}

ensure_release_branch_current() {
    local current_branch head_commit remote_commit

    current_branch="$(git -C "$REPO_ROOT" symbolic-ref --quiet --short HEAD)" ||
        fail "正式打包必須在 $RELEASE_BRANCH 分支上執行。"
    [[ "$current_branch" == "$RELEASE_BRANCH" ]] ||
        fail "正式打包必須在 $RELEASE_BRANCH 分支上執行，目前為 $current_branch。"

    if ! remote_commit="$(
        git -C "$REPO_ROOT" ls-remote "$RELEASE_REMOTE" "refs/heads/$RELEASE_BRANCH"
    )"; then
        fail "無法讀取 $RELEASE_REMOTE/$RELEASE_BRANCH。"
    fi

    remote_commit="${remote_commit%%$'\t'*}"
    [[ -n "$remote_commit" ]] ||
        fail "找不到遠端分支 $RELEASE_REMOTE/$RELEASE_BRANCH。"

    head_commit="$(git -C "$REPO_ROOT" rev-parse HEAD)"
    [[ "$head_commit" == "$remote_commit" ]] ||
        fail "本機 $RELEASE_BRANCH 與 $RELEASE_REMOTE/$RELEASE_BRANCH 不同步，請先同步後再打包。"
}

ensure_github_cli_authenticated() {
    gh auth status --hostname github.com >/dev/null 2>&1 ||
        fail "GitHub CLI 尚未登入，請先執行 gh auth login。"
}

ensure_release_target_available() {
    local tag="$1"

    if git -C "$REPO_ROOT" rev-parse --verify --quiet "refs/tags/$tag" >/dev/null; then
        fail "本機已存在版本 tag: $tag"
    fi

    if git -C "$REPO_ROOT" ls-remote --exit-code --tags "$RELEASE_REMOTE" "refs/tags/$tag" \
        >/dev/null 2>&1; then
        fail "遠端已存在版本 tag: $tag"
    fi

    if gh release view "$tag" --repo "$GITHUB_REPOSITORY_SLUG" >/dev/null 2>&1; then
        fail "GitHub Release 已存在: $tag"
    fi
}

create_release_commit() {
    local tag="$1"

    git -C "$REPO_ROOT" add -- "$APP_PROJECT_REL" "$INSTALLER_SCRIPT_REL"
    if git -C "$REPO_ROOT" diff --cached --quiet -- "$APP_PROJECT_REL" "$INSTALLER_SCRIPT_REL"; then
        fail "找不到可提交的版本檔案變更。"
    fi

    git -C "$REPO_ROOT" commit -m "release: $tag"
    release_version_committed=true
}

push_release_commit_and_tag() {
    local tag="$1"

    git -C "$REPO_ROOT" tag -a "$tag" -m "Release $tag"
    git -C "$REPO_ROOT" push --atomic "$RELEASE_REMOTE" \
        "HEAD:refs/heads/$RELEASE_BRANCH" "refs/tags/$tag" ||
        fail "無法原子推送版本提交與 tag。已保留本機提交和 tag，請確認後重試。"
}

create_github_release() {
    local tag="$1"
    local installer="$2"
    local checksum="$3"

    if ! gh release create "$tag" "$installer" "$checksum" \
        --repo "$GITHUB_REPOSITORY_SLUG" \
        --verify-tag \
        --title "CS2 Focus Guard $tag" \
        --generate-notes; then
        printf '\nGitHub Release 尚未建立。遠端版本提交與 tag 已保留，可修正問題後執行：\n' >&2
        printf 'gh release create %q %q %q --repo %q --verify-tag --title %q --generate-notes\n' \
            "$tag" "$installer" "$checksum" "$GITHUB_REPOSITORY_SLUG" "CS2 Focus Guard $tag" >&2
        fail "無法建立 GitHub Release。"
    fi
}

create_installer_checksum() {
    local installer="$1"
    local checksum="$2"
    local hash

    hash="$(sha256sum "$installer")"
    hash="${hash%% *}"
    printf '%s  %s\n' "$hash" "$(basename "$installer")" > "$checksum"
}

next_patch_version() {
    local current_version major minor patch

    current_version="$(
        sed -nE 's|^[[:space:]]*<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>[[:space:]]*$|\1|p' \
            "$APP_PROJECT"
    )"

    if [[ "$current_version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
        major="${BASH_REMATCH[1]}"
        minor="${BASH_REMATCH[2]}"
        patch="${BASH_REMATCH[3]}"
    else
        fail "無法從 $APP_PROJECT_REL 讀取三段式版本號。"
    fi

    printf '%s.%s.%s\n' "$major" "$minor" "$((10#$patch + 1))"
}

release_version() {
    if [[ -n "$release_version_override" ]]; then
        printf '%s\n' "$release_version_override"
    else
        next_patch_version
    fi
}

update_version_files() {
    local version="$1"

    grep -Eq '^[[:space:]]*<Version>[0-9]+\.[0-9]+\.[0-9]+</Version>[[:space:]]*$' "$APP_PROJECT" ||
        fail "找不到應用程式版本欄位。"
    grep -Eq '^#define MyAppVersion "[0-9]+\.[0-9]+\.[0-9]+"$' "$INSTALLER_SCRIPT" ||
        fail "找不到安裝程式版本欄位。"

    sed -i -E \
        "s|(<Version>)[0-9]+\.[0-9]+\.[0-9]+(</Version>)|\\1${version}\\2|" \
        "$APP_PROJECT"
    sed -i -E \
        "s|^(#define MyAppVersion \")[0-9]+\.[0-9]+\.[0-9]+(\")$|\\1${version}\\2|" \
        "$INSTALLER_SCRIPT"

    grep -Fqx "    <Version>$version</Version>" "$APP_PROJECT" ||
        fail "應用程式版本更新失敗。"
    grep -Fqx "#define MyAppVersion \"$version\"" "$INSTALLER_SCRIPT" ||
        fail "安裝程式版本更新失敗。"
}

find_iscc() {
    local candidate
    local -a candidates=(
        "${ISCC:-}"
        "$(command -v ISCC.exe 2>/dev/null || true)"
        "$(command -v ISCC 2>/dev/null || true)"
        "/c/Program Files (x86)/Inno Setup 6/ISCC.exe"
        "/c/Program Files/Inno Setup 6/ISCC.exe"
    )

    for candidate in "${candidates[@]}"; do
        if [[ -n "$candidate" && -f "$candidate" ]]; then
            printf '%s\n' "$candidate"
            return
        fi
    done

    fail "找不到 Inno Setup 編譯器。請設定 ISCC，或安裝 Inno Setup 6。"
}

cleanup_release_version_files() {
    local status=$?

    trap - EXIT

    if [[ "$release_versions_modified" == true &&
        "$release_version_committed" == false &&
        "$status" -ne 0 ]]; then
        cp "$release_backup_dir/CS2FocusGuard.App.csproj" "$APP_PROJECT"
        cp "$release_backup_dir/CS2FocusGuard.iss" "$INSTALLER_SCRIPT"
        git -C "$REPO_ROOT" reset --quiet -- "$APP_PROJECT_REL" "$INSTALLER_SCRIPT_REL"
        printf '正式打包失敗，已還原版本檔案。\n' >&2
    fi

    if [[ -n "$release_backup_dir" && -d "$release_backup_dir" ]]; then
        rm -rf "$release_backup_dir"
    fi

    exit "$status"
}

run_release_build() {
    local version tag iscc installer checksum

    require_command dotnet
    require_command git
    require_command gh
    require_command sha256sum
    ensure_version_files_clean
    ensure_release_worktree_clean
    ensure_release_branch_current
    ensure_github_cli_authenticated

    printf '正在執行 Release 測試...\n'
    dotnet test "$REPO_ROOT/CS2FocusGuard.sln" -c Release --nologo

    version="$(release_version)"
    tag="v$version"
    ensure_release_target_available "$tag"
    iscc="$(find_iscc)"

    release_backup_dir="$(mktemp -d)"
    cp "$APP_PROJECT" "$release_backup_dir/CS2FocusGuard.App.csproj"
    cp "$INSTALLER_SCRIPT" "$release_backup_dir/CS2FocusGuard.iss"
    trap cleanup_release_version_files EXIT

    printf '正在將版本號更新為 %s...\n' "$version"
    release_versions_modified=true
    update_version_files "$version"

    printf '正在發佈單檔 win-x64 應用程式...\n'
    dotnet publish "$APP_PROJECT" \
        -c Release \
        -r win-x64 \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=false \
        -o "$PUBLISH_DIR" \
        --nologo

    printf '正在編譯 Inno Setup 安裝程式...\n'
    "$iscc" "$INSTALLER_SCRIPT"

    installer="$ARTIFACT_DIR/CS2FocusGuard-Setup-$version-x64.exe"
    [[ -f "$installer" ]] || fail "找不到預期的安裝程式: $installer"
    checksum="$installer.sha256"
    create_installer_checksum "$installer" "$checksum"

    printf '正在建立版本提交...\n'
    create_release_commit "$tag"

    printf '正在建立並推送版本 tag...\n'
    push_release_commit_and_tag "$tag"

    printf '正在建立 GitHub Release 並上傳安裝程式...\n'
    create_github_release "$tag" "$installer" "$checksum"

    printf '\n正式版本已建立並發布: %s\n' "$installer"
    printf 'GitHub Release: https://github.com/%s/releases/tag/%s\n' \
        "$GITHUB_REPOSITORY_SLUG" "$tag"
}

parse_arguments() {
    while [[ "$#" -gt 0 ]]; do
        case "$1" in
            --version)
                [[ "$#" -ge 2 ]] ||
                    fail "--version 必須指定三段式版本號，例如 --version 1.0.0。"
                release_version_override="$2"
                shift 2
                ;;
            --version=*)
                release_version_override="${1#--version=}"
                shift
                ;;
            *)
                fail "不支援的參數: $1"
                ;;
        esac
    done

    if [[ -n "$release_version_override" &&
        ! "$release_version_override" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        fail "版本號必須是三段式數字，例如 1.0.0。"
    fi
}

main() {
    parse_arguments "$@"

    printf 'CS2 Focus Guard 建置工具\n'
    printf '1) 本機編譯並啟動 Debug 應用程式\n'
    printf '2) 打包正式 Release 版本並自動遞增 patch 版號\n'
    if [[ -n "$release_version_override" ]]; then
        printf '指定正式版版本號: %s\n' "$release_version_override"
    fi
    printf '請選擇 [1-2]: '

    local choice
    read -r choice

    case "$choice" in
        1) run_local_build ;;
        2) run_release_build ;;
        *) fail "請輸入 1 或 2。" ;;
    esac
}

main "$@"
