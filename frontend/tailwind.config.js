/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        // 核心层级表面 (Surfaces)
        background: '#141513',
        sidebar: '#1C1E1B',
        card: '#242623',
        'card-hover': '#292C28',
        secondary: '#292C28',
        'secondary-hover': '#323530',
        
        // 边框描边 (Borders)
        border: '#3B3E38',
        'border-hover': '#4F524A',
        'border-focus': '#BAA17E',

        // 文本色彩 (Typography)
        foreground: '#EDEBE5',
        muted: '#A4A59F',
        'muted-foreground': '#6F706B',

        // 核心强调色 - 香槟暖铜金 (Accent / Brand)
        accent: {
          DEFAULT: '#BAA17E',
          hover: '#C9B393',
          active: '#A88E6B',
          foreground: '#171815',
          muted: 'rgba(186, 161, 126, 0.12)',
        },
        primary: {
          DEFAULT: '#BAA17E',
          hover: '#C9B393',
          active: '#A88E6B',
          foreground: '#171815',
        },

        // 低饱和语义色 (Semantic States)
        success: {
          DEFAULT: '#88A982',
          bg: '#1E251E',
          border: '#334333',
          foreground: '#EDEBE5',
        },
        warning: {
          DEFAULT: '#CBA167',
          bg: '#262118',
          border: '#4B3D28',
          foreground: '#EFE3D0',
        },
        destructive: {
          DEFAULT: '#C96B64',
          bg: '#261B1B',
          border: '#4E2E2C',
          foreground: '#F2DCDA',
        },
        info: {
          DEFAULT: '#7B96A8',
          bg: '#192025',
          border: '#2E3C47',
          foreground: '#EDEBE5',
        },
      },
      borderRadius: {
        card: '12px',
        btn: '8px',
        badge: '6px',
      },
      fontFamily: {
        sans: [
          'Inter',
          '-apple-system',
          'BlinkMacSystemFont',
          '"Segoe UI"',
          'Roboto',
          '"PingFang SC"',
          '"Microsoft YaHei"',
          'sans-serif',
        ],
        mono: [
          '"JetBrains Mono"',
          '"SF Mono"',
          'Consolas',
          'Menlo',
          'Monaco',
          'monospace',
        ],
      },
      boxShadow: {
        panel: '0 12px 32px rgba(0, 0, 0, 0.45)',
        tooltip: '0 4px 16px rgba(0, 0, 0, 0.5)',
      },
    },
  },
  plugins: [],
}
