import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { Toasts } from './shared/toasts';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, Toasts],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {}
