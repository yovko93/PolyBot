/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        neon: '#39ff14',
        panel: '#070b07'
      },
      boxShadow: {
        neon: '0 0 8px rgba(57,255,20,0.25)'
      }
    }
  },
  plugins: []
};
