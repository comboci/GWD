# R02 — چرخهٔ عمر Wizard
تست StatusCard به مالک واقعی StatusCardUi منتقل شد. هشت میزبان واقعی، بازسازی Review، قطع و تعویض callback آزمایشی Scan/Location، بقای state پروژه و Log مشترک، و توقف Timer پوسته در Dispose کنترل می‌شوند؛ هیچ IIS/SQL/Deploy فراخوانی نمی‌شود.
شواهد: WizardForm.Navigation.cs، UserControlهای UI/Pages، StatusCardUi.cs و متن تست. اجرای آزمون: NOT_RUN طبق POLICY.
