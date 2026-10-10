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
        // 核心层级表面 (Surfaces) - 严格引用 CSS 变量
        background: 'var(--background)',
        sidebar: 'var(--sidebar)',
        card: {
          DEFAULT: 'var(--card)',
          hover: 'var(--card-hover)',
        },
        secondary: {
          DEFAULT: 'var(--secondary)',
          hover: 'var(--secondary-hover)',
        },
        
        // 边框描边 (Borders)
        border: {
          DEFAULT: 'var(--border)',
          hover: 'var(--border-hover)',
          focus: 'var(--border-focus)',
        },

        // 文本色彩 (Typography - WCAG AA 合规)
        foreground: 'var(--foreground)',
        muted: {
          DEFAULT: 'var(--muted)',
          foreground: 'var(--muted-foreground)',
        },

        // 核心强调色 - 香槟暖铜金 (Brand & Accent)
        accent: {
          DEFAULT: 'var(--accent)',
          hover: 'var(--accent-hover)',
          active: 'var(--accent-active)',
          foreground: 'var(--accent-foreground)',
          muted: 'var(--accent-muted)',
        },
        primary: {
          DEFAULT: 'var(--accent)',
          hover: 'var(--accent-hover)',
          active: 'var(--accent-active)',
          foreground: 'var(--accent-foreground)',
        },

        // 低饱和语义色 (Semantic States)
        success: {
          DEFAULT: 'var(--success)',
          bg: 'var(--success-bg)',
          border: 'var(--success-border)',
          foreground: 'var(--success-foreground)',
        },
        warning: {
          DEFAULT: 'var(--warning)',
          bg: 'var(--warning-bg)',
          border: 'var(--warning-border)',
          foreground: 'var(--warning-foreground)',
        },
        destructive: {
          DEFAULT: 'var(--destructive)',
          bg: 'var(--destructive-bg)',
          border: 'var(--destructive-border)',
          foreground: 'var(--destructive-foreground)',
        },
        info: {
          DEFAULT: 'var(--info)',
          bg: 'var(--info-bg)',
          border: 'var(--info-border)',
          foreground: 'var(--info-foreground)',
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
