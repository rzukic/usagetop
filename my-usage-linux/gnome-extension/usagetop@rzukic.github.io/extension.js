// usagetop in GNOME's top bar: the combined level as a ring, then the limits
// closest to running out, and a click that opens the app's panel under it.
//
// GNOME gives apps no place in its bar, so this is where the Mac's menu bar
// item lives here. It draws nothing of its own invention: the app decides what
// a bar shows (the "bar" list of `usagetop status --json`), and this asks it
// over the app's socket. With no app running it starts one, tray-less.

import Cairo from 'cairo';
import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import St from 'gi://St';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';

const REFRESH_SECONDS = 30;
const SOCKET = GLib.build_filenamev([GLib.get_user_runtime_dir(), 'usagetop.sock']);

/** One line to the app, its answer back; null when no app answers. Never blocks the shell. */
function ask(command, cancellable) {
    return new Promise(resolve => {
        const client = new Gio.SocketClient({timeout: 3});
        client.connect_async(Gio.UnixSocketAddress.new(SOCKET), cancellable, (source, result) => {
            let connection;
            try {
                connection = source.connect_finish(result);
            } catch {
                resolve(null);
                return;
            }
            const chunks = [];
            const input = connection.get_input_stream();
            const read = () => input.read_bytes_async(65536, GLib.PRIORITY_DEFAULT, cancellable, (stream, done) => {
                try {
                    const bytes = stream.read_bytes_finish(done);
                    if (bytes.get_size() > 0) {
                        chunks.push(new TextDecoder().decode(bytes.toArray()));
                        read();
                        return;
                    }
                    resolve(chunks.join(''));
                } catch {
                    resolve(null);
                }
                connection.close_async(GLib.PRIORITY_DEFAULT, null, null);
            });
            connection.get_output_stream().write_bytes_async(
                new GLib.Bytes(new TextEncoder().encode(`${command}\n`)), GLib.PRIORITY_DEFAULT, cancellable,
                (stream, done) => {
                    try {
                        stream.write_bytes_finish(done);
                        // The app reads one line and answers once it sees this side is done talking.
                        connection.get_socket().shutdown(false, true);
                        read();
                    } catch {
                        resolve(null);
                    }
                });
        });
    });
}

/**
 * Up to two letters, in the case they were written in: the first of each of
 * the first two words, or of a single word its first ("#2" stays as it is).
 * The Mac's AccountBadge.initials.
 */
function initials(name) {
    const words = name.split(/[\s\-_·]+/).filter(Boolean);
    if (words.length >= 2) return words[0][0] + words[1][0];
    const word = words[0] ?? '';
    if (word.length <= 2) return word;
    return /^[#0-9]/.test(word) ? word.slice(0, 3) : word[0];
}

/** Where the app's program is: on the PATH, or where a user's own copy usually lives. */
function findApp() {
    const home = GLib.get_home_dir();
    const places = [
        GLib.find_program_in_path('usagetop'),
        GLib.build_filenamev([home, '.local', 'bin', 'usagetop']),
        GLib.build_filenamev([home, 'Applications', 'usagetop', 'usagetop']),
        '/usr/bin/usagetop',
        '/opt/usagetop/usagetop',
    ];
    return places.find(path => path && GLib.file_test(path, GLib.FileTest.IS_EXECUTABLE)) ?? null;
}

/** A level as a ring that empties clockwise, in the bar's own colour unless it warns. */
const Ring = GObject.registerClass(
class Ring extends St.DrawingArea {
    _init() {
        super._init({style_class: 'usagetop-ring', y_align: Clutter.ActorAlign.CENTER});
        this._percent = null;
        this._tone = 'normal';
    }

    setLevel(percent, tone) {
        this._percent = percent;
        this._tone = tone;
        this.queue_repaint();
    }

    vfunc_repaint() {
        const cr = this.get_context();
        const [width, height] = this.get_surface_size();
        const ink = this.get_theme_node().get_foreground_color();
        const stroke = Math.max(2, Math.round(Math.min(width, height) * 0.16));
        const radius = (Math.min(width, height) - stroke) / 2;

        cr.setLineWidth(stroke);
        cr.setSourceRGBA(ink.red / 255, ink.green / 255, ink.blue / 255, 0.3);
        cr.arc(width / 2, height / 2, radius, 0, 2 * Math.PI);
        cr.stroke();

        if (this._percent > 0) {
            const tones = {warning: [0.957, 0.796, 0.478], critical: [0.961, 0.565, 0.533]};
            const [r, g, b] = tones[this._tone] ?? [ink.red / 255, ink.green / 255, ink.blue / 255];
            const fraction = Math.min(this._percent / 100, 1);
            cr.setSourceRGBA(r, g, b, 1);
            cr.setLineCap(Cairo.LineCap.ROUND);
            cr.arc(width / 2, height / 2, radius, -Math.PI / 2, -Math.PI / 2 + fraction * 2 * Math.PI);
            cr.stroke();
        }
        cr.$dispose();
    }
});

const Indicator = GObject.registerClass(
class Indicator extends PanelMenu.Button {
    _init(extension) {
        // No menu of its own: the app's panel is what opens.
        super._init(0.5, 'usagetop', true);
        this._extension = extension;
        this._cancellable = new Gio.Cancellable();
        this._startedApp = 0;

        this._box = new St.BoxLayout({style_class: 'usagetop-box', y_align: Clutter.ActorAlign.CENTER});
        this.add_child(this._box);
        this._draw(null);

        this.connect('button-press-event', () => this._toggle());
        this.connect('touch-event', (actor, event) =>
            event.type() === Clutter.EventType.TOUCH_BEGIN ? this._toggle() : Clutter.EVENT_PROPAGATE);
        this.connect('key-press-event', (actor, event) => {
            const key = event.get_key_symbol();
            return key === Clutter.KEY_Return || key === Clutter.KEY_space ? this._toggle() : Clutter.EVENT_PROPAGATE;
        });

        this._refresh();
        this._timer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, REFRESH_SECONDS, () => {
            this._refresh();
            return GLib.SOURCE_CONTINUE;
        });
    }

    /** Where this item is on the screen, so the panel can open under it. */
    _anchor() {
        const [x, y] = this.get_transformed_position();
        const [width, height] = this.get_transformed_size();
        return [x, y, width, height].map(Math.round).join(',');
    }

    _toggle() {
        ask(`toggle --anchor ${this._anchor()}`, this._cancellable).then(answer => {
            if (answer !== null) {
                this._refreshWhileInUse();
                return;
            }
            // Nothing answered: start the app, and open its panel once it is up.
            if (this._startApp()) {
                this._openSoon = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1500, () => {
                    this._openSoon = 0;
                    ask(`show --anchor ${this._anchor()}`, this._cancellable).then(() => this._refreshWhileInUse());
                    return GLib.SOURCE_REMOVE;
                });
            }
        });
        return Clutter.EVENT_STOP;
    }

    /**
     * With the panel just opened, something is likely to change in it (a
     * refresh, sample data, a hidden limit), and the bar should not lag half a
     * minute behind: it looks again a few times, soon.
     */
    _refreshWhileInUse() {
        this._refresh();
        if (this._soon) GLib.source_remove(this._soon);
        const waits = [2, 3, 5, 10];
        const next = () => {
            if (waits.length === 0) {
                this._soon = 0;
                return;
            }
            this._soon = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, waits.shift(), () => {
                this._refresh();
                next();
                return GLib.SOURCE_REMOVE;
            });
        };
        next();
    }

    _startApp() {
        // Not more than once a minute: an app that cannot start is not hammered.
        const now = GLib.get_monotonic_time();
        if (now - this._startedApp < 60 * 1000 * 1000) return false;
        const program = findApp();
        if (!program) return false;
        this._startedApp = now;
        try {
            Gio.Subprocess.new([program, '--hidden'], Gio.SubprocessFlags.NONE);
            return true;
        } catch (error) {
            console.warn(`usagetop: could not start ${program}: ${error.message}`);
            return false;
        }
    }

    _refresh() {
        ask('status --json', this._cancellable).then(answer => {
            if (this._cancellable.is_cancelled()) return;
            if (answer === null) {
                this._draw(null);
                if (this._startApp()) {
                    this._retry = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 2500, () => {
                        this._retry = 0;
                        this._refresh();
                        return GLib.SOURCE_REMOVE;
                    });
                }
                return;
            }
            try {
                this._draw(JSON.parse(answer));
            } catch {
                this._draw(null);
            }
        });
    }

    /** The bar's items as the app lists them; a quiet ring while there is no reading. */
    _draw(status) {
        this._box.destroy_all_children();
        const items = status?.bar ?? [];
        if (items.length === 0) {
            const ring = new Ring();
            ring.add_style_class_name('usagetop-quiet');
            this._box.add_child(ring);
            this.accessible_name = 'usagetop';
            return;
        }

        const spoken = [];
        for (const item of items) {
            const group = new St.BoxLayout({style_class: 'usagetop-item', y_align: Clutter.ActorAlign.CENTER});
            if (item.kind === 'combined') {
                const ring = new Ring();
                ring.setLevel(item.percent, item.class);
                group.add_child(ring);
                spoken.push(item.title ?? `${item.text} left in all`);
            } else {
                const file = this._extension.dir.get_child('icons').get_child(`usagetop-${item.provider}-symbolic.svg`);
                // The mark, and for an account with a name of its own its
                // initials in a badge on the mark's corner, as T3 marks a project.
                const mark = new St.Widget({
                    layout_manager: new Clutter.BinLayout(),
                    style_class: 'usagetop-mark-box',
                    y_align: Clutter.ActorAlign.CENTER,
                });
                mark.add_child(new St.Icon({
                    gicon: new Gio.FileIcon({file}),
                    style_class: 'usagetop-mark',
                    x_align: Clutter.ActorAlign.START,
                    y_align: Clutter.ActorAlign.START,
                }));
                if (item.account) {
                    mark.add_child(new St.Label({
                        text: initials(item.account),
                        style_class: 'usagetop-badge',
                        x_align: Clutter.ActorAlign.END,
                        y_align: Clutter.ActorAlign.END,
                    }));
                }
                group.add_child(mark);
                spoken.push(`${item.title} ${item.text}`);
            }
            // The ring alone when Settings asks for no figure beside it.
            if (item.text) {
                const figure = new St.Label({
                    text: item.text,
                    style_class: 'usagetop-figure',
                    y_align: Clutter.ActorAlign.CENTER,
                });
                if (item.class === 'warning' || item.class === 'critical') {
                    figure.add_style_class_name(`usagetop-${item.class}`);
                }
                group.add_child(figure);
            }
            this._box.add_child(group);
        }
        this.accessible_name = `usagetop: ${spoken.join(', ')}`;
    }

    destroy() {
        this._cancellable.cancel();
        for (const source of [this._timer, this._openSoon, this._retry, this._soon]) {
            if (source) GLib.source_remove(source);
        }
        this._timer = this._openSoon = this._retry = this._soon = 0;
        super.destroy();
    }
});

export default class UsagetopExtension extends Extension {
    enable() {
        this._indicator = new Indicator(this);
        Main.panel.addToStatusArea(this.uuid, this._indicator, 0, 'right');
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}
