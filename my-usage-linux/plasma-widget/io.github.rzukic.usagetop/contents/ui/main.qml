// usagetop in a Plasma panel: the combined level as a ring, or the limits
// closest to running out, each as its provider's mark with the limit's name
// over what is left of it; a click opens the app's panel by it.
//
// As the GNOME extension's indicator.js, which this mirrors: it draws nothing
// of its own invention. The app decides what a bar shows (the "bar" list of
// `usagetop status --json --bar`, whose --bar also tells the app that its tray
// icon can stand aside), and with no app running this starts one, tray-less.
// No popup of its own: the app's panel is what opens.

import QtQuick
import QtQuick.Layouts
import QtQuick.Shapes
import QtQuick.Window
import org.kde.kirigami as Kirigami
import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.plasma5support as P5Support
import org.kde.plasma.plasmoid

PlasmoidItem {
    id: root

    readonly property int refreshSeconds: 30
    // The bar's items as the app last listed them; empty while there is no reading.
    property var items: []
    // Whether the app answered the last time it was asked.
    property bool appAnswers: false
    property double startedAppAt: 0
    property int serial: 0

    readonly property bool vertical: Plasmoid.formFactor === PlasmaCore.Types.Vertical

    Plasmoid.backgroundHints: PlasmaCore.Types.NoBackground
    preferredRepresentation: compactRepresentation
    fullRepresentation: Item {}
    toolTipMainText: "usagetop"
    toolTipSubText: items.map(item => item.kind === "combined"
        ? (item.title || "")
        : `${item.title} ${item.text}`).join("\n")

    // ---- The app, from a shell ----

    // Where the app's program is: on the PATH, or where a user's own copy
    // usually lives (indicator.js's findApp). Plasma's own PATH may leave out
    // ~/.local/bin.
    readonly property string findApp: 'u=$(command -v usagetop) || for u in "$HOME/.local/bin/usagetop" '
        + '"$HOME/Applications/usagetop/usagetop" /usr/bin/usagetop /opt/usagetop/usagetop ""; do '
        + '[ -x "$u" ] && break; done; [ -n "$u" ] || exit 127; '

    P5Support.DataSource {
        id: shell
        engine: "executable"
        connectedSources: []
        property var replies: ({})

        onNewData: (source, data) => {
            const reply = replies[source];
            delete replies[source];
            disconnectSource(source);
            if (reply) reply(data["exit code"], data["stdout"] || "");
        }

        // Runs a line of shell with the app found as $u; the reply has its exit code and output.
        function run(line, reply) {
            // Each its own source, or two alike at once would be taken for one.
            const source = `${root.findApp}${line} #${++root.serial}`;
            replies[source] = reply || null;
            connectSource(source);
        }
    }

    // Started in the background, with nothing of it left on the shell's pipes.
    readonly property string startApp: '("$u" --hidden > /dev/null 2>&1 < /dev/null &); '

    function startAppOnce() {
        // Not more than once a minute: an app that cannot start is not hammered.
        const now = Date.now();
        if (now - startedAppAt < 60 * 1000) return false;
        startedAppAt = now;
        shell.run(startApp + "exit 0");
        return true;
    }

    function refresh() {
        shell.run('exec "$u" status --json --bar', (code, out) => {
            let status = null;
            if (code === 0) {
                try { status = JSON.parse(out); } catch (e) { status = null; }
            }
            appAnswers = status !== null;
            items = status && Array.isArray(status.bar) ? status.bar : [];
            if (code !== 0 && code !== 127 && startAppOnce()) retry.restart();
        });
    }

    Timer {
        interval: root.refreshSeconds * 1000
        running: true
        repeat: true
        triggeredOnStart: true
        onTriggered: root.refresh()
    }
    Timer { id: retry; interval: 2500; onTriggered: root.refresh() }

    // With the panel just opened, something is likely to change in it (a
    // refresh, sample data, a hidden limit), and the bar should not lag half a
    // minute behind: it looks again a few times, soon.
    property var soonWaits: []
    Timer {
        id: soon
        onTriggered: {
            root.refresh();
            if (root.soonWaits.length > 0) {
                interval = root.soonWaits.shift() * 1000;
                restart();
            }
        }
    }
    function refreshWhileInUse() {
        soonWaits = [3, 5, 10];
        soon.interval = 2000;
        soon.restart();
    }

    // "x,y,width,height" of this item in the screen's pixels, as the app's --anchor takes it.
    function anchor(item) {
        const corner = item.mapToGlobal(0, 0);
        const scale = Screen.devicePixelRatio || 1;
        return [corner.x, corner.y, item.width, item.height].map(n => Math.round(n * scale)).join(",");
    }

    function toggle(item) {
        const where = anchor(item);
        if (appAnswers) {
            // In the background: with no app answering after all, the command
            // would become the app and never finish.
            shell.run(`("$u" toggle --anchor ${where} > /dev/null 2>&1 < /dev/null &)`);
        } else {
            // Nothing answered: start the app, and open its panel once it is up.
            shell.run('"$u" status > /dev/null 2>&1 || { ' + startApp
                + 'for i in $(seq 40); do sleep 0.25; "$u" status > /dev/null 2>&1 && break; done; }; '
                + `"$u" status > /dev/null 2>&1 && ("$u" show --anchor ${where} > /dev/null 2>&1 < /dev/null &)`);
        }
        refreshWhileInUse();
    }

    // ---- Its look, as the GNOME bar's ----

    // A panel's ink and the colours a figure warns in, lighter on a dark panel.
    readonly property color ink: Kirigami.Theme.textColor
    readonly property bool darkPanel: {
        const c = Kirigami.Theme.backgroundColor;
        return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b < 0.5;
    }
    function tone(cls) {
        if (cls === "warning") return darkPanel ? "#f4cb7a" : "#E0A33C";
        if (cls === "critical") return darkPanel ? "#f59088" : "#E05A50";
        return ink;
    }
    // Up to two letters, in the case they were written in: the Mac's AccountBadge.initials.
    function initials(name) {
        const words = name.split(/[\s\-_·]+/).filter(Boolean);
        if (words.length >= 2) return words[0][0] + words[1][0];
        const word = words[0] || "";
        if (word.length <= 2) return word;
        return /^[#0-9]/.test(word) ? word.slice(0, 3) : word[0];
    }

    compactRepresentation: MouseArea {
        id: compact

        // Sizes from the GNOME bar's, grown with the panel up to a point: two
        // lines, the name and the figure, fill it as they do there. Plasma
        // gives a widget 28 of a default panel's 44.
        readonly property real thickness: root.vertical ? width : height
        readonly property real k: Math.max(0.9, Math.min(1.6, thickness / 24))
        readonly property string family: Kirigami.Theme.defaultFont.family

        Layout.minimumWidth: root.vertical ? -1 : row.implicitWidth + Math.round(6 * k)
        Layout.preferredWidth: Layout.minimumWidth
        Layout.minimumHeight: root.vertical ? row.implicitHeight + Math.round(6 * k) : -1
        Layout.preferredHeight: Layout.minimumHeight

        acceptedButtons: Qt.LeftButton
        hoverEnabled: true
        onClicked: root.toggle(compact)

        GridLayout {
            id: row
            anchors.centerIn: parent
            flow: root.vertical ? GridLayout.TopToBottom : GridLayout.LeftToRight
            rows: root.vertical ? -1 : 1
            columns: root.vertical ? 1 : -1
            columnSpacing: Math.round(9 * compact.k)
            rowSpacing: Math.round(6 * compact.k)

            // A quiet ring while there is no reading.
            Ring {
                visible: root.items.length === 0
                size: Math.round(16 * compact.k)
                percent: 0
                opacity: 0.6
            }

            Repeater {
                model: root.items

                RowLayout {
                    id: entry
                    required property var modelData
                    readonly property bool isLimit: modelData.kind === "limit"
                    spacing: Math.round(4 * compact.k)

                    Ring {
                        visible: !entry.isLimit
                        size: Math.round(16 * compact.k)
                        percent: entry.modelData.percent || 0
                        fill: root.tone(entry.modelData["class"])
                    }

                    // The mark, and for an account with a name of its own its
                    // initials in a badge hanging off the mark's bottom-right
                    // corner, as T3 marks a project: moved, not laid out, so
                    // the mark keeps its place and its size.
                    Item {
                        visible: entry.isLimit
                        readonly property int size: Math.round(15 * compact.k)
                        Layout.preferredWidth: size
                        Layout.preferredHeight: size
                        Layout.rightMargin: badge.visible ? Math.round(3 * compact.k) : 0
                        Layout.alignment: Qt.AlignVCenter

                        Kirigami.Icon {
                            anchors.fill: parent
                            source: entry.isLimit
                                ? Qt.resolvedUrl(`../icons/usagetop-${entry.modelData.provider}-symbolic.svg`) : ""
                            isMask: true
                            color: root.ink
                        }
                        // The bar's ink, cut round by the bar's own colour so it stands apart.
                        Rectangle {
                            id: badge
                            visible: !!entry.modelData.account
                            readonly property real pad: Math.max(1, Math.round(1 * compact.k))
                            width: Math.max(badgeText.implicitWidth + 2 * pad + 2 * border.width, height)
                            height: badgeText.implicitHeight + 2 * border.width
                            radius: Math.round(4 * compact.k)
                            color: root.ink
                            border.width: 1
                            border.color: Kirigami.Theme.backgroundColor
                            x: parent.width - width + Math.round(4 * compact.k)
                            y: parent.height - height + Math.round(3 * compact.k)
                            Text {
                                id: badgeText
                                anchors.centerIn: parent
                                text: root.initials(entry.modelData.account || "")
                                color: Kirigami.Theme.backgroundColor
                                font.family: compact.family
                                font.pixelSize: Math.max(6, Math.round(6.5 * compact.k))
                                font.weight: Font.Black
                                topPadding: 0
                                bottomPadding: 0
                                lineHeightMode: Text.FixedHeight
                                lineHeight: font.pixelSize
                            }
                        }
                    }

                    // Which limit it is ("Weekly", "5h", "Fable") over what is
                    // left of it, a little fainter, as the Mac's menu bar has it.
                    ColumnLayout {
                        visible: !!entry.modelData.text
                        spacing: 0
                        Layout.alignment: Qt.AlignVCenter

                        Text {
                            visible: entry.isLimit && !!entry.modelData.label
                            text: entry.modelData.label || ""
                            color: root.ink
                            opacity: 0.72
                            font.family: compact.family
                            font.pixelSize: Math.round(9 * compact.k)
                            font.weight: Font.DemiBold
                            lineHeightMode: Text.FixedHeight
                            lineHeight: Math.round(10 * compact.k)
                            Layout.alignment: Qt.AlignLeft
                        }
                        Text {
                            readonly property bool stacked: entry.isLimit && !!entry.modelData.label
                            text: entry.modelData.text || ""
                            color: root.tone(entry.modelData["class"])
                            font.family: compact.family
                            font.pixelSize: Math.round((stacked ? 12 : 13) * compact.k)
                            font.weight: Font.DemiBold
                            font.features: { "tnum": 1 }
                            lineHeightMode: Text.FixedHeight
                            lineHeight: Math.round((stacked ? 13 : 15) * compact.k)
                            Layout.alignment: Qt.AlignLeft
                        }
                    }
                }
            }
        }
    }

    // A level as a ring that empties clockwise, in the panel's ink unless it warns.
    component Ring: Item {
        id: ring
        property int size: 16
        property real percent: 0
        property color fill: root.ink
        readonly property real stroke: Math.max(2, Math.round(size * 0.16))
        readonly property real radius: (size - stroke) / 2
        Layout.preferredWidth: size
        Layout.preferredHeight: size
        Layout.alignment: Qt.AlignVCenter

        Shape {
            anchors.fill: parent
            preferredRendererType: Shape.CurveRenderer
            ShapePath {
                strokeColor: Qt.rgba(root.ink.r, root.ink.g, root.ink.b, 0.3)
                strokeWidth: ring.stroke
                fillColor: "transparent"
                PathAngleArc {
                    centerX: ring.size / 2; centerY: ring.size / 2
                    radiusX: ring.radius; radiusY: ring.radius
                    startAngle: 0; sweepAngle: 360
                }
            }
            ShapePath {
                strokeColor: ring.percent > 0 ? ring.fill : "transparent"
                strokeWidth: ring.stroke
                fillColor: "transparent"
                capStyle: ShapePath.RoundCap
                PathAngleArc {
                    centerX: ring.size / 2; centerY: ring.size / 2
                    radiusX: ring.radius; radiusY: ring.radius
                    startAngle: -90; sweepAngle: Math.min(ring.percent / 100, 1) * 360
                }
            }
        }
    }
}
