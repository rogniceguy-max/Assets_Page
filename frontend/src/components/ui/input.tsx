import * as React from 'react';
import { cn } from '@/lib/utils';

export interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {}

const Input = React.forwardRef<HTMLInputElement, InputProps>(
  ({ className, type, ...props }, ref) => {
    return (
      <input
        type={type}
        className={cn(
          // 规格要求：正文 14px, 控件圆角 8px, 1px 低对比度描边, 聚焦高亮为香槟铜金
          'flex h-11 w-full rounded-[8px] border border-border bg-sidebar px-3.5 py-2 text-sm text-foreground placeholder:text-muted-foreground',
          'transition-colors duration-150',
          'file:border-0 file:bg-transparent file:text-sm file:font-medium',
          'focus-visible:outline-none focus-visible:border-accent focus-visible:ring-1 focus-visible:ring-accent/50',
          'disabled:cursor-not-allowed disabled:opacity-40',
          className
        )}
        ref={ref}
        {...props}
      />
    );
  }
);
Input.displayName = 'Input';

export { Input };
