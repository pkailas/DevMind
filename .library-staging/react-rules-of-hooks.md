# Source: react.dev/src\content\reference\rules\rules-of-hooks.md

---
title: Rules of Hooks
---

<Intro>
Hooks are defined using JavaScript functions, but they represent a special type of reusable UI logic with restrictions on where they can be called.
</Intro>

<InlineToc />

---

##  Only call Hooks at the top level {/*only-call-hooks-at-the-top-level*/}

Functions whose names start with `use` are called [*Hooks*](/reference/react) in React.

**Don’t call Hooks inside loops, conditions, nested functions, or `try`/`catch`/`finally` blocks.** Instead, always use Hooks at the top level of your React function, before any early returns. You can only call Hooks while React is rendering a function component:

* ✅ Call them at the top level in the body of a [function component](/learn/your-first-component).
* ✅ Call them at the top level in the body of a [custom Hook](/learn/reusing-logic-with-custom-hooks).

```js{2-3,8-9}
function Counter() {
  // ✅ Good: top-level in a function component
  const [count, setCount] = useState(0);
  // ...
}

function useWindowWidth() {
  // ✅ Good: top-level in a custom Hook
  const [width, setWidth] = useState(window.innerWidth);
  // ...
}
```

It’s **not** supported to call Hooks (functions starting with `use`) in any other cases, for example:

* 🔴 Do not call Hooks inside conditions or loops.
* 🔴 Do not call Hooks after a conditional `return` statement.
* 🔴 Do not call Hooks in event handlers.
* 🔴 Do not call Hooks in class components.
* 🔴 Do not call Hooks inside functions passed to `useMemo`, `useReducer`, or `useEffect`.
* 🔴 Do not call Hooks inside `try`/`catch`/`finally` blocks.

If you break these rules, you might see this error.

```js{3-4,11-12,20-21}
function Bad({ cond }) {
  if (cond) {
    // 🔴 Bad: inside a condition (to fix, move it outside!)
    const theme = useContext(ThemeContext);
  }
  // ...
}

function Bad() {
  for (let i = 0; i < 10; i++) {
    // 🔴 Bad: inside a loop (to fix, move it outside!)
    const theme = useContext(ThemeContext);
  }
  // ...
}

function Bad({ cond }) {
  if (cond) {
    return;
  }
  // 🔴 Bad: after a conditional return (to fix, move it before the return!)
  const theme = useContext(ThemeContext);
  // ...
}

function Bad() {
  function handleClick() {
    // 🔴 Bad: inside an event handler (to fix, move it outside!)
    const theme = useContext(ThemeContext);
  }
  // ...
}

function Bad() {
  const style = useMemo(() => {
    // 🔴 Bad: inside useMemo (to fix, move it outside!)
    const theme = useContext(ThemeContext);
    return createStyle(theme);
  });
  // ...
}

class Bad extends React.Component {
  render() {
    // 🔴 Bad: inside a class component (to fix, write a function component instead of a class!)
    useEffect(() => {})
    // ...
  }
}

function Bad() {
  try {
    // 🔴 Bad: inside try/catch/finally block (to fix, move it outside!)
    const [x, setX] = useState(0);
  } catch {
    const [x, setX] = useState(1);
  }
}
```

You can use the [`eslint-plugin-react-hooks` plugin](https://www.npmjs.com/package/eslint-plugin-react-hooks) to catch these mistakes.

<Note>

[Custom Hooks](/learn/reusing-logic-with-custom-hooks) *may* call other Hooks (that's their whole purpose). This works because custom Hooks are also supposed to only be called while a function component is rendering.

</Note>

---

## Only call Hooks from React functions {/*only-call-hooks-from-react-functions*/}

Don’t call Hooks from regular JavaScript functions. Instead, you can:

✅ Call Hooks from React function components.
✅ Call Hooks from [custom Hooks](/learn/reusing-logic-with-custom-hooks#extracting-your-own-custom-hook-from-a-component).

By following this rule, you ensure that all stateful logic in a component is clearly visible from its source code.

```js {2,5}
function FriendList() {
  const [onlineStatus, setOnlineStatus] = useOnlineStatus(); // ✅
}

function setOnlineStatus() { // ❌ Not a component or custom Hook!
  const [onlineStatus, setOnlineStatus] = useOnlineStatus();
}
```


# Source: react.dev/src\content\reference\rules\components-and-hooks-must-be-pure.md

---
title: Components and Hooks must be pure
---

<Intro>
Pure functions only perform a calculation and nothing more. It makes your code easier to understand, debug, and allows React to automatically optimize your components and Hooks correctly.
</Intro>

<Note>
This reference page covers advanced topics and requires familiarity with the concepts covered in the [Keeping Components Pure](/learn/keeping-components-pure) page.
</Note>

<InlineToc />

### Why does purity matter? {/*why-does-purity-matter*/}

One of the key concepts that makes React, _React_ is _purity_. A pure component or hook is one that is:

* **Idempotent** – You [always get the same result every time](/learn/keeping-components-pure#purity-components-as-formulas) you run it with the same inputs – props, state, context for component inputs; and arguments for hook inputs.
* **Has no side effects in render** – Code with side effects should run [**separately from rendering**](#how-does-react-run-your-code). For example as an [event handler](/learn/responding-to-events) – where the user interacts with the UI and causes it to update; or as an [Effect](/reference/react/useEffect) – which runs after render.
* **Does not mutate non-local values**: Components and Hooks should [never modify values that aren't created locally](#mutation) in render.

When render is kept pure, React can understand how to prioritize which updates are most important for the user to see first. This is made possible because of render purity: since components don't have side effects [in render](#how-does-react-run-your-code), React can pause rendering components that aren't as important to update, and only come back to them later when it's needed.

Concretely, this means that rendering logic can be run multiple times in a way that allows React to give your user a pleasant user experience. However, if your component has an untracked side effect – like modifying the value of a global variable [during render](#how-does-react-run-your-code) – when React runs your rendering code again, your side effects will be triggered in a way that won't match what you want. This often leads to unexpected bugs that can degrade how your users experience your app. You can see an [example of this in the Keeping Components Pure page](/learn/keeping-components-pure#side-effects-unintended-consequences).

#### How does React run your code? {/*how-does-react-run-your-code*/}

React is declarative: you tell React _what_ to render, and React will figure out _how_ best to display it to your user. To do this, React has a few phases where it runs your code. You don't need to know about all of these phases to use React well. But at a high level, you should know about what code runs in _render_, and what runs outside of it.

_Rendering_ refers to calculating what the next version of your UI should look like. After rendering, React takes this new calculation and compares it to the calculation used to create the previous version of your UI. Then React commits just the minimum changes needed to the [DOM](https://developer.mozilla.org/en-US/docs/Web/API/Document_Object_Model) (what your user actually sees) to apply the changes. Finally, [Effects](/learn/synchronizing-with-effects) are flushed (meaning they are run until there are no more left). For more detailed information see the docs for [Render](/learn/render-and-commit) and [Commit and Effect Hooks](/reference/react/hooks#effect-hooks).

<DeepDive>

#### How to tell if code runs in render {/*how-to-tell-if-code-runs-in-render*/}

One quick heuristic to tell if code runs during render is to examine where it is: if it's written at the top level like in the example below, there's a good chance it runs during render.

```js {2}
function Dropdown() {
  const selectedItems = new Set(); // created during render
  // ...
}
```

Event handlers and Effects don't run in render:

```js {4}
function Dropdown() {
  const selectedItems = new Set();
  const onSelect = (item) => {
    // this code is in an event handler, so it's only run when the user triggers this
    selectedItems.add(item);
  }
}
```

```js {4}
function Dropdown() {
  const selectedItems = new Set();
  useEffect(() => {
    // this code is inside of an Effect, so it only runs after rendering
    logForAnalytics(selectedItems);
  }, [selectedItems]);
}
```
</DeepDive>

---

## Components and Hooks must be idempotent {/*components-and-hooks-must-be-idempotent*/}

Components must always return the same output with respect to their inputs – props, state, and context. This is known as _idempotency_. [Idempotency](https://en.wikipedia.org/wiki/Idempotence) is a term popularized in functional programming. It refers to the idea that you [always get the same result every time](learn/keeping-components-pure) you run that piece of code with the same inputs.

This means that _all_ code that runs [during render](#how-does-react-run-your-code) must also be idempotent in order for this rule to hold. For example, this line of code is not idempotent (and therefore, neither is the component):

```js {2}
function Clock() {
  const time = new Date(); // 🔴 Bad: always returns a different result!
  return <span>{time.toLocaleString()}</span>
}
```

`new Date()` is not idempotent as it always returns the current date and changes its result every time it's called. When you render the above component, the time displayed on the screen will stay stuck on the time that the component was rendered. Similarly, functions like `Math.random()` also aren't idempotent, because they return different results every time they're called, even when the inputs are the same.

This doesn't mean you shouldn't use non-idempotent functions like `new Date()` _at all_ – you should just avoid using them [during render](#how-does-react-run-your-code). In this case, we can _synchronize_ the latest date to this component using an [Effect](/reference/react/useEffect):



By wrapping the non-idempotent `new Date()` call in an Effect, it moves that calculation [outside of rendering](#how-does-react-run-your-code).

If you don't need to synchronize some external state with React, you can also consider using an [event handler](/learn/responding-to-events) if it only needs to be updated in response to a user interaction.

---

## Side effects must run outside of render {/*side-effects-must-run-outside-of-render*/}

[Side effects](/learn/keeping-components-pure#side-effects-unintended-consequences) should not run [in render](#how-does-react-run-your-code), as React can render components multiple times to create the best possible user experience.

<Note>
Side effects are a broader term than Effects. Effects specifically refer to code that's wrapped in `useEffect`, while a side effect is a general term for code that has any observable effect other than its primary result of returning a value to the caller.

Side effects are typically written inside of [event handlers](/learn/responding-to-events) or Effects. But never during render.
</Note>

While render must be kept pure, side effects are necessary at some point in order for your app to do anything interesting, like showing something on the screen! The key point of this rule is that side effects should not run [in render](#how-does-react-run-your-code), as React can render components multiple times. In most cases, you'll use [event handlers](learn/responding-to-events) to handle side effects. Using an event handler explicitly tells React that this code doesn't need to run during render, keeping render pure. If you've exhausted all options – and only as a last resort – you can also handle side effects using `useEffect`.

### When is it okay to have mutation? {/*mutation*/}

#### Local mutation {/*local-mutation*/}
One common example of a side effect is mutation, which in JavaScript refers to changing the value of a non-[primitive](https://developer.mozilla.org/en-US/docs/Glossary/Primitive) value. In general, while mutation is not idiomatic in React, _local_ mutation is absolutely fine:

```js {2,7}
function FriendList({ friends }) {
  const items = []; // ✅ Good: locally created
  for (let i = 0; i < friends.length; i++) {
    const friend = friends[i];
    items.push(
      <Friend key={friend.id} friend={friend} />
    ); // ✅ Good: local mutation is okay
  }
  return <section>{items}</section>;
}
```

There is no need to contort your code to avoid local mutation. [`Array.map`](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Array/map) could also be used here for brevity, but there is nothing wrong with creating a local array and then pushing items into it [during render](#how-does-react-run-your-code).

Even though it looks like we are mutating `items`, the key point to note is that this code only does so _locally_ – the mutation isn't "remembered" when the component is rendered again. In other words, `items` only stays around as long as the component does. Because `items` is always _recreated_ every time `<FriendList />` is rendered, the component will always return the same result.

On the other hand, if `items` was created outside of the component, it holds on to its previous values and remembers changes:

```js {1,7}
const items = []; // 🔴 Bad: created outside of the component
function FriendList({ friends }) {
  for (let i = 0; i < friends.length; i++) {
    const friend = friends[i];
    items.push(
      <Friend key={friend.id} friend={friend} />
    ); // 🔴 Bad: mutates a value created outside of render
  }
  return <section>{items}</section>;
}
```

When `<FriendList />` runs again, we will continue appending `friends` to `items` every time that component is run, leading to multiple duplicated results. This version of `<FriendList />` has observable side effects [during render](#how-does-react-run-your-code) and **breaks the rule**.

#### Lazy initialization {/*lazy-initialization*/}

Lazy initialization is also fine despite not being fully "pure":

```js {2}
function ExpenseForm() {
  SuperCalculator.initializeIfNotReady(); // ✅ Good: if it doesn't affect other components
  // Continue rendering...
}
```

#### Changing the DOM {/*changing-the-dom*/}

Side effects that are directly visible to the user are not allowed in the render logic of React components. In other words, merely calling a component function shouldn’t by itself produce a change on the screen.

```js {2}
function ProductDetailPage({ product }) {
  document.title = product.title; // 🔴 Bad: Changes the DOM
}
```

One way to achieve the desired result of updating `document.title` outside of render is to [synchronize the component with `document`](/learn/synchronizing-with-effects).

As long as calling a component multiple times is safe and doesn’t affect the rendering of other components, React doesn’t care if it’s 100% pure in the strict functional programming sense of the word. It is more important that [components must be idempotent](/reference/rules/components-and-hooks-must-be-pure).

---

## Props and state are immutable {/*props-and-state-are-immutable*/}

A component's props and state are immutable [snapshots](learn/state-as-a-snapshot). Never mutate them directly. Instead, pass new props down, and use the setter function from `useState`.

You can think of the props and state values as snapshots that are updated after rendering. For this reason, you don't modify the props or state variables directly: instead you pass new props, or use the setter function provided to you to tell React that state needs to update the next time the component is rendered.

### Don't mutate Props {/*props*/}
Props are immutable because if you mutate them, the application will produce inconsistent output, which can be hard to debug as it may or may not work depending on the circumstances.

```js {expectedErrors: {'react-compiler': [2]}} {2}
function Post({ item }) {
  item.url = new Url(item.url, base); // 🔴 Bad: never mutate props directly
  return <Link url={item.url}>{item.title}</Link>;
}
```

```js {2}
function Post({ item }) {
  const url = new Url(item.url, base); // ✅ Good: make a copy instead
  return <Link url={url}>{item.title}</Link>;
}
```

### Don't mutate State {/*state*/}
`useState` returns the state variable and a setter to update that state.

```js
const [stateVariable, setter] = useState(0);
```

Rather than updating the state variable in-place, we need to update it using the setter function that is returned by `useState`. Changing values on the state variable doesn't cause the component to update, leaving your users with an outdated UI. Using the setter function informs React that the state has changed, and that we need to queue a re-render to update the UI.

```js {expectedErrors: {'react-compiler': [2, 5]}} {5}
function Counter() {
  const [count, setCount] = useState(0);

  function handleClick() {
    count = count + 1; // 🔴 Bad: never mutate state directly
  }

  return (
    <button onClick={handleClick}>
      You pressed me {count} times
    </button>
  );
}
```

```js {5}
function Counter() {
  const [count, setCount] = useState(0);

  function handleClick() {
    setCount(count + 1); // ✅ Good: use the setter function returned by useState
  }

  return (
    <button onClick={handleClick}>
      You pressed me {count} times
    </button>
  );
}
```

---

## Return values and arguments to Hooks are immutable {/*return-values-and-arguments-to-hooks-are-immutable*/}

Once values are passed to a hook, you should not modify them. Like props in JSX, values become immutable when passed to a hook.

```js {expectedErrors: {'react-compiler': [4]}} {4}
function useIconStyle(icon) {
  const theme = useContext(ThemeContext);
  if (icon.enabled) {
    icon.className = computeStyle(icon, theme); // 🔴 Bad: never mutate hook arguments directly
  }
  return icon;
}
```

```js {3}
function useIconStyle(icon) {
  const theme = useContext(ThemeContext);
  const newIcon = { ...icon }; // ✅ Good: make a copy instead
  if (icon.enabled) {
    newIcon.className = computeStyle(icon, theme);
  }
  return newIcon;
}
```

One important principle in React is _local reasoning_: the ability to understand what a component or hook does by looking at its code in isolation. Hooks should be treated like "black boxes" when they are called. For example, a custom hook might have used its arguments as dependencies to memoize values inside it:

```js {4}
function useIconStyle(icon) {
  const theme = useContext(ThemeContext);

  return useMemo(() => {
    const newIcon = { ...icon };
    if (icon.enabled) {
      newIcon.className = computeStyle(icon, theme);
    }
    return newIcon;
  }, [icon, theme]);
}
```

If you were to mutate the Hook's arguments, the custom hook's memoization will become incorrect,  so it's important to avoid doing that.

```js {4}
style = useIconStyle(icon);         // `style` is memoized based on `icon`
icon.enabled = false;               // Bad: 🔴 never mutate hook arguments directly
style = useIconStyle(icon);         // previously memoized result is returned
```

```js {4}
style = useIconStyle(icon);         // `style` is memoized based on `icon`
icon = { ...icon, enabled: false }; // Good: ✅ make a copy instead
style = useIconStyle(icon);         // new value of `style` is calculated
```

Similarly, it's important to not modify the return values of Hooks, as they may have been memoized.

---

## Values are immutable after being passed to JSX {/*values-are-immutable-after-being-passed-to-jsx*/}

Don't mutate values after they've been used in JSX. Move the mutation to before the JSX is created.

When you use JSX in an expression, React may eagerly evaluate the JSX before the component finishes rendering. This means that mutating values after they've been passed to JSX can lead to outdated UIs, as React won't know to update the component's output.

```js {expectedErrors: {'react-compiler': [4]}} {4}
function Page({ colour }) {
  const styles = { colour, size: "large" };
  const header = <Header styles={styles} />;
  styles.size = "small"; // 🔴 Bad: styles was already used in the JSX above
  const footer = <Footer styles={styles} />;
  return (
    <>
      {header}
      <Content />
      {footer}
    </>
  );
}
```

```js {4}
function Page({ colour }) {
  const headerStyles = { colour, size: "large" };
  const header = <Header styles={headerStyles} />;
  const footerStyles = { colour, size: "small" }; // ✅ Good: we created a new value
  const footer = <Footer styles={footerStyles} />;
  return (
    <>
      {header}
      <Content />
      {footer}
    </>
  );
}
```


# Source: react.dev/src\content\reference\rules\react-calls-components-and-hooks.md

---
title: React calls Components and Hooks
---

<Intro>
React is responsible for rendering components and Hooks when necessary to optimize the user experience. It is declarative: you tell React what to render in your component’s logic, and React will figure out how best to display it to your user.
</Intro>

<InlineToc />

---

## Never call component functions directly {/*never-call-component-functions-directly*/}
Components should only be used in JSX. Don't call them as regular functions. React should call it.

React must decide when your component function is called [during rendering](/reference/rules/components-and-hooks-must-be-pure#how-does-react-run-your-code). In React, you do this using JSX.

```js {2}
function BlogPost() {
  return <Layout><Article /></Layout>; // ✅ Good: Only use components in JSX
}
```

```js {expectedErrors: {'react-compiler': [2]}} {2}
function BlogPost() {
  return <Layout>{Article()}</Layout>; // 🔴 Bad: Never call them directly
}
```

If a component contains Hooks, it's easy to violate the [Rules of Hooks](/reference/rules/rules-of-hooks) when components are called directly in a loop or conditionally.

Letting React orchestrate rendering also allows a number of benefits:

* **Components become more than functions.** React can augment them with features like _local state_ through Hooks that are tied to the component's identity in the tree.
* **Component types participate in reconciliation.** By letting React call your components, you also tell it more about the conceptual structure of your tree. For example, when you move from rendering `<Feed>` to the `<Profile>` page, React won’t attempt to re-use them.
* **React can enhance your user experience.** For example, it can let the browser do some work between component calls so that re-rendering a large component tree doesn’t block the main thread.
* **A better debugging story.** If components are first-class citizens that the library is aware of, we can build rich developer tools for introspection in development.
* **More efficient reconciliation.** React can decide exactly which components in the tree need re-rendering and skip over the ones that don't. That makes your app faster and more snappy.

---

## Never pass around Hooks as regular values {/*never-pass-around-hooks-as-regular-values*/}

Hooks should only be called inside of components or Hooks. Never pass it around as a regular value.

Hooks allow you to augment a component with React features. They should always be called as a function, and never passed around as a regular value. This enables _local reasoning_, or the ability for developers to understand everything a component can do by looking at that component in isolation.

Breaking this rule will cause React to not automatically optimize your component.

### Don't dynamically mutate a Hook {/*dont-dynamically-mutate-a-hook*/}

Hooks should be as "static" as possible. This means you shouldn't dynamically mutate them. For example, this means you shouldn't write higher order Hooks:

```js {expectedErrors: {'react-compiler': [2, 3]}} {2}
function ChatInput() {
  const useDataWithLogging = withLogging(useData); // 🔴 Bad: don't write higher order Hooks
  const data = useDataWithLogging();
}
```

Hooks should be immutable and not be mutated. Instead of mutating a Hook dynamically, create a static version of the Hook with the desired functionality.

```js {2,6}
function ChatInput() {
  const data = useDataWithLogging(); // ✅ Good: Create a new version of the Hook
}

function useDataWithLogging() {
  // ... Create a new version of the Hook and inline the logic here
}
```

### Don't dynamically use Hooks {/*dont-dynamically-use-hooks*/}

Hooks should also not be dynamically used: for example, instead of doing dependency injection in a component by passing a Hook as a value:

```js {expectedErrors: {'react-compiler': [2]}} {2}
function ChatInput() {
  return <Button useData={useDataWithLogging} /> // 🔴 Bad: don't pass Hooks as props
}
```

You should always inline the call of the Hook into that component and handle any logic in there.

```js {6}
function ChatInput() {
  return <Button />
}

function Button() {
  const data = useDataWithLogging(); // ✅ Good: Use the Hook directly
}

function useDataWithLogging() {
  // If there's any conditional logic to change the Hook's behavior, it should be inlined into
  // the Hook
}
```

This way, `<Button />` is much easier to understand and debug. When Hooks are used in dynamic ways, it increases the complexity of your app greatly and inhibits local reasoning, making your team less productive in the long term. It also makes it easier to accidentally break the [Rules of Hooks](/reference/rules/rules-of-hooks) that Hooks should not be called conditionally. If you find yourself needing to mock components for tests, it's better to mock the server instead to respond with canned data. If possible, it's also usually more effective to test your app with end-to-end tests.



