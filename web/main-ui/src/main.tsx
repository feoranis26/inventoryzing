import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MantineProvider, createTheme } from '@mantine/core'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import '@mantine/core/styles.css'
import '@fontsource/ibm-plex-sans/400.css'
import '@fontsource/ibm-plex-sans/500.css'
import '@fontsource/ibm-plex-sans/600.css'
import '@fontsource/ibm-plex-mono/400.css'
import './styles.css'
import App from './InventoryApp'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: false, staleTime: 10_000 } },
})

const theme = createTheme({
  fontFamily: 'IBM Plex Sans, sans-serif',
  fontFamilyMonospace: 'IBM Plex Mono, monospace',
  primaryColor: 'workshop',
  primaryShade: 7,
  colors: {
    workshop: ['#edf7f3', '#d7ede3', '#add9c5', '#80c4a7', '#59b38f', '#3ea77f', '#2e9970', '#187653', '#146451', '#0b513e'],
  },
  defaultRadius: 'sm',
  headings: { fontFamily: 'IBM Plex Sans, sans-serif', sizes: { h1: { fontSize: '1.75rem' }, h2: { fontSize: '1.2rem' } } },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MantineProvider theme={theme}>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </MantineProvider>
  </StrictMode>,
)
