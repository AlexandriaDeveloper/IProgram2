import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class VideoService {
    private url: string | null = null;
    async getVideoUrl(path = 'assets/images/bg/SciFi_01.mov'): Promise<string> {
        if (this.url) return this.url;
        console.log('fetching video', this.url);
        const res = await fetch(path, { cache: 'force-cache' });
        const blob = await res.blob();
        this.url = URL.createObjectURL(blob);
        return this.url;
    }
    revoke(): void {
        if (this.url) {
            try { URL.revokeObjectURL(this.url); } catch {}
            this.url = null;
        }
    }
}