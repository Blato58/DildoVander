# DildoVander development workspace

The application and its companion deployment checkout are separate Git
repositories. Keep them as siblings under a non-Git development directory; do
not initialize Git in the parent or combine their histories. The PlatformIO
project for the kufr is only `firmware/` in the application repository.

## Updating an existing checkout after the layout change

Application components now live directly at the repository root. The old
`kufr/` container is removed; its documentation is merged into `docs/` and its
simulator is grouped with the production code under `firmware/simulator/`.
Shared protocols and UI assets remain in `contracts/` and `design-system/`.

Recreate local dependency/build outputs at their new paths with the component's
normal restore/build commands. Old `.pio`, `node_modules`, `bin` and `obj`
directories are local caches, not source to move or commit. From the updated
application checkout, refresh the generated workspace paths and managed tasks:

```powershell
.\devtools\workspace\Sync-Workspace.ps1 -RefreshManagedTasks
```

The setup migrates the known former firmware folder path while retaining
unrelated workspace settings and custom tasks. A firmware folder pointing to
another custom location still requires resolving that name conflict explicitly.
The source layout does not change service names, package identifiers, public
routes, published artifact names or existing model/archive provenance.

## Fresh setup

1. Clone the application repository and, for team members with access, the
   independently maintained deployment repository into sibling directories.
   The deployment checkout is optional for application and firmware builds.
2. Install VS Code, Git, Python, Node.js, a host C++14 compiler and PlatformIO
   Core 6.2.0. Install the recommended VS Code extensions when prompted.
   ESP-IDF 6.1 and the Espressif PlatformIO platform are selected by
   `firmware/platformio.ini`; PlatformIO installs the project packages.
3. From the application clone, run:

   ```powershell
   .\devtools\workspace\Sync-Workspace.ps1
   ```

   The script detects the parent from its own location. To place the generated
   workspace at another existing parent containing the application checkout,
   pass `-WorkspacePath <path>`. It creates or merges the parent `.vscode` settings,
   tasks, extension recommendations, and `DildoVander.code-workspace`. Existing
   settings, tasks, launch configurations, and recommendations are retained.
   Existing task labels are not replaced; remove or rename a conflicting task
   to adopt the supplied version. To refresh an unmodified generated task, use
   `-RefreshManagedTasks`.
4. Open the common parent:

   ```powershell
   code .
   ```

   This folder mode supplies repository-root tasks, file navigation, and Git
   discovery for repositories in child folders. Parent settings request nested
   repository scanning; Source Control still shows the repositories as
   independent roots. No Git repository is created in the parent.
5. For native PlatformIO project discovery, open the generated multi-root
   workspace instead:

   ```powershell
   code DildoVander.code-workspace
   ```

   The workspace includes the common parent as `DildoVander` and the actual
   project as `Firmware`. The PlatformIO extension therefore opens a folder
   containing the sole kufr `platformio.ini`; folder mode does not claim that
   the extension automatically discovers nested projects.

## Firmware tasks and CLI

The un-suffixed tasks in the parent folder are for `code .`. In the multi-root
workspace, use the corresponding `(Multi-root)` tasks; their project path
explicitly uses the named `Firmware` workspace folder.

The default build task is **Firmware: Build Production**. **Firmware: Build
All** sequentially builds production and all five diagnostics. Builds never
upload. **Firmware: Upload Diagnostic** prompts for a diagnostic environment;
upload tasks are separate explicit actions. **Firmware: Monitor** prompts for
an environment and opens the serial monitor. **Firmware: Clean** cleans the six
environment build outputs.

Equivalent commands from the common parent are:

```powershell
pio project config -d DildoVander-app/firmware --lint
pio run -d DildoVander-app/firmware
pio run -d DildoVander-app/firmware -e diag-servo-bench
pio run -d DildoVander-app/firmware -e diag-servo-bench -t upload
pio device monitor -d DildoVander-app/firmware -e diag-servo-bench
```

From inside `firmware`, the equivalent commands omit `-d`:

```powershell
pio run
pio run -e diag-servo-bench
pio run -e diag-servo-bench -t upload
pio device monitor -e diag-servo-bench
```

Use **Firmware: Run Tests** for the existing native C++, Python, and servo-bench
JavaScript suites. It invokes their existing runners rather than routing tests
through PlatformIO. The C++ host checks require a local C++14 compiler; the
show replay checks also require Node.js and the `contracts` npm
dependencies.

The servo-bench panel is a loopback-only static server. Run **Servo Bench: Start
Panel**, then open `http://127.0.0.1:8765/` in a supported browser. The server
does not open a serial port; connect to the board from the browser panel when
needed. Its service is local to that development machine.

## Build configuration and source ownership

`src/CMakeLists.txt` is the application-source authority, not PlatformIO's
`build_src_filter`. PlatformIO supplies an environment-specific SDK config
filename; the root CMake project uses that environment name to select one
application entry point, exact source set, component requirements, and
`SDKCONFIG_DEFAULTS`. Each PlatformIO environment has a separate `.pio/build`
directory and a separate generated `compile_commands.json`.

The firmware `.vscode/c_cpp_properties.json` selects those generated compile
databases per environment. Build the selected environment before choosing its
IntelliSense configuration. This avoids manually copying ESP-IDF, managed
component, and generated SDK include paths into editor settings.

Firmware entry points are under `src/production/` and
`src/diagnostics/<name>/`. Diagnostic documentation, configuration generators,
provisioning utilities, browser panel resources, and diagnostic-specific tests
remain under `diagnostics/<name>/`; they are source support, not expendable
build output. Shared ESP-IDF components live in `lib/`, and generated managed
components and build products remain ignored.

Firmware release tooling consumes the production image. Diagnostic images are
not OTA release artifacts. Do not dispatch deployment or flash hardware as part
of a build or test task. Host builds do not validate physical wiring, motion,
serial hardware, or debugger-probe availability.
