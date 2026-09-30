import SwiftUI

/// The recorder's window: choose a window, record it, take pictures of it.
///
/// One column, controls in the order they are used, every label complete on its own. The
/// status line changes only when something happens — never on a clock — so VoiceOver is not
/// handed a value that changes under its cursor every second.
struct ContentView: View {
    @ObservedObject var model: RecorderModel
    @ObservedObject var guide: Guide
    @ObservedObject var mapPoints: MapPointsController

    private static let keysHelp = """
        While the game is in front, with Control-Shift-Command: G starts or stops the guide, \
        O says the objective, W says where the guide leads, D describes what's on the map, S marks this spot, B takes you back to it, \
        equals makes the beacon louder, minus quieter, N starts a new map, R starts or stops recording, \
        M marks the moment, P saves a picture, \
        L scans the open map's points and steps to the next, K steps back, J points at the chosen one and reads its tooltip, J again clicks it.
        """

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if model.permissionMissing {
                Text("Screen Recording is not allowed yet. Allow Dungeon Guide in Privacy and Security, then reopen it.")
                    .fixedSize(horizontal: false, vertical: true)
                Button("Open Screen Recording settings") { model.openScreenRecordingSettings() }
                Button("Quit Dungeon Guide") { NSApp.terminate(nil) }
            }

            Picker("Game window", selection: $model.selectedID) {
                Text("No window chosen").tag(CGWindowID?.none)
                ForEach(model.windows) { window in
                    Text(window.label).tag(CGWindowID?.some(window.id))
                }
            }
            .disabled(model.isRecording || guide.isOn)

            Button("Refresh the window list") { Task { await model.refresh() } }
                .disabled(model.isRecording || guide.isOn)

            Button(guide.isOn ? "Stop the guide" : "Start the guide") {
                if guide.isOn { guide.stop() } else { Task { await guide.start(await model.captureTarget()) } }
            }
            .disabled(!guide.isOn && model.selectedID == nil)

            Button("Say the objective") { guide.sayObjective() }
                .disabled(!guide.isOn)

            Button("Say where the guide leads") { guide.sayWhere() }
                .disabled(!guide.isOn)

            Button("Describe what's on the map") { guide.describeMap() }
                .disabled(!guide.isOn)

            Button("Mark this spot") { guide.markSpot() }
                .disabled(!guide.isOn)

            Button("Take me back to the marked spot") { guide.takeMeBack() }
                .disabled(!guide.isOn)

            Button("Start a new map") { guide.newMap() }
                .disabled(!guide.isOn)

            Button("Scan the open map's points") { Task { await mapPoints.scan() } }
            Button("Next map point") { Task { await mapPoints.next() } }
            Button("Previous map point") { Task { await mapPoints.previous() } }
            Button("Point at the chosen map point, or click it") { Task { await mapPoints.goToPoint() } }
            if mapPoints.index >= 0, mapPoints.index < mapPoints.points.count {
                Text("Map point \(mapPoints.index + 1) of \(mapPoints.points.count): \(mapPoints.describeChosen())")
                    .fixedSize(horizontal: false, vertical: true)
            }

            Slider(value: Binding(get: { guide.beaconVolume }, set: { guide.beaconVolume = $0 }), in: 0...1) {
                Text("Beacon volume")
            }

            Toggle("Vibration", isOn: $guide.vibration)
            Toggle("Lead to beacons for time", isOn: $guide.leadToBeacons)

            Button("Test the vibration: left, right, then on course") { guide.testVibration() }

            Text(guide.status)
                .fixedSize(horizontal: false, vertical: true)

            Button(model.isRecording ? "Stop recording" : "Start recording") { model.toggleRecording() }
                .disabled(!model.isRecording && model.selectedID == nil)

            Button("Mark this moment") { model.mark() }
                .disabled(!model.isRecording)

            Button("Save a picture of the window") { Task { await model.savePicture() } }
                .disabled(model.selectedID == nil)

            Button("Open the recordings folder") { model.openFolder() }

            Text(model.status)
                .fixedSize(horizontal: false, vertical: true)

            Text(Self.keysHelp)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(20)
        .frame(minWidth: 480, alignment: .leading)
    }
}
