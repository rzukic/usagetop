// usagetop's place in GNOME's top bar. This file only loads the item itself,
// indicator.js, and loads it again whenever it changes on disk.
//
// GNOME Shell imports a module once per file for as long as it runs, and on
// Wayland it only runs anew after a logout: an update of the app would put a new
// indicator.js beside this one and the bar would keep drawing the old one until
// then. So each version is copied to a file named after its contents, which the
// shell has never seen, and imported from there. This loader is meant never to
// change, so that its own copy in memory is never the old one.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import St from 'gi://St';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';

export default class UsagetopExtension extends Extension {
    enable() {
        this._loaded = null;
        this._generation = 0;
        // The app's updater and install.sh copy the extension's files over in
        // one go: a moment after the last change, look once.
        this._monitor = this.dir.monitor_directory(Gio.FileMonitorFlags.NONE, null);
        this._monitor.connect('changed', () => {
            if (this._debounce) GLib.source_remove(this._debounce);
            this._debounce = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1000, () => {
                this._debounce = 0;
                this._load();
                return GLib.SOURCE_REMOVE;
            });
        });
        this._load();
    }

    disable() {
        this._generation++;
        this._monitor?.cancel();
        this._monitor = null;
        if (this._debounce) GLib.source_remove(this._debounce);
        this._debounce = 0;
        this._indicator?.destroy();
        this._indicator = null;
        this._loaded = null;
    }

    async _load() {
        const generation = ++this._generation;
        try {
            const [, code] = this.dir.get_child('indicator.js').load_contents(null);
            const style = this.dir.get_child('stylesheet.css');
            const [, css] = style.query_exists(null) ? style.load_contents(null) : [true, new Uint8Array()];
            const name = GLib.compute_checksum_for_data(GLib.ChecksumType.SHA256, code).slice(0, 16);
            const both = name + GLib.compute_checksum_for_data(GLib.ChecksumType.SHA256, css).slice(0, 16);
            if (both === this._loaded) return;

            const cache = Gio.File.new_for_path(GLib.build_filenamev([GLib.get_user_cache_dir(), 'usagetop', 'gnome-extension']));
            GLib.mkdir_with_parents(cache.get_path(), 0o700);
            const copy = cache.get_child(`indicator-${name}.js`);
            if (!copy.query_exists(null)) {
                copy.replace_contents(code, null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
            }
            const module = await import(copy.get_uri());
            if (generation !== this._generation) return;

            // The shell read the stylesheet when the extension was switched on;
            // a changed one is read again before the new item is drawn.
            if (this._loaded !== null && style.query_exists(null)) {
                const theme = St.ThemeContext.get_for_stage(global.stage).get_theme();
                theme.unload_stylesheet(style);
                theme.load_stylesheet(style);
            }

            this._indicator?.destroy();
            this._indicator = new module.Indicator(this);
            Main.panel.addToStatusArea(this.uuid, this._indicator, 0, 'right');
            this._loaded = both;
            this._tidy(cache, copy);
        } catch (error) {
            console.error(`usagetop: could not load the top bar item: ${error}`);
        }
    }

    /** Earlier copies are loaded already or never will be again: they go. */
    _tidy(cache, keep) {
        try {
            const children = cache.enumerate_children('standard::name', Gio.FileQueryInfoFlags.NONE, null);
            for (let info; (info = children.next_file(null));) {
                const child = cache.get_child(info.get_name());
                if (!child.equal(keep) && info.get_name().startsWith('indicator-')) child.delete(null);
            }
        } catch {
            // A copy left behind costs a few kilobytes and nothing else.
        }
    }
}
