import { test, expect } from '@playwright/test';

/**
 * PayPal checkout E2E tests. Require ESHOP_PAYPAL_E2E_TEST_MODE=1 so the WebApp
 * skips the real PayPal API and simulates create-order + return flow (session validation).
 */
test.describe('PayPal checkout', () => {
  test('create PayPal order from checkout, return with valid session, place order with PayPalOrderId', async ({
    page,
  }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Ready for a new adventure?' })).toBeVisible();

    // Add item to basket and go to checkout
    await page.getByRole('link', { name: 'Adventurer GPS Watch' }).click();
    await page.getByRole('button', { name: 'Add to shopping bag' }).click();
    await page.getByRole('link', { name: 'shopping bag' }).click();
    await expect(page.getByRole('heading', { name: 'Shopping bag' })).toBeVisible();
    await page.getByRole('link', { name: 'Check out' }).click();

    await expect(page.getByRole('heading', { name: 'Checkout' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Pay with PayPal' })).toBeVisible();

    // Create PayPal order (in E2E test mode this redirects to /paypal/return then checkout with paid=1)
    await page.getByRole('link', { name: 'Pay with PayPal' }).click();

    // Return with valid session: we land on checkout with paid=1 and matching session PayPalOrderId
    await expect(page).toHaveURL(/\/checkout/);
    await expect(
      page.getByText('Your PayPal authorization was successful. You can now place your order.')
    ).toBeVisible();
    await expect(page.getByRole('button', { name: 'Place order' })).toBeVisible();

    // Fill shipping address if not pre-filled
    await page.getByLabel('Address').fill('123 Test St');
    await page.getByLabel('City').first().fill('Test City');
    await page.getByLabel('State').first().fill('TS');
    await page.getByLabel('Zip code').first().fill('12345');
    await page.getByLabel('Country').fill('Test Country');

    // Place order with PayPalOrderId (stored from return flow)
    await page.getByRole('button', { name: 'Place order' }).click();

    // Order placed; redirect to user orders
    await expect(page).toHaveURL(/\/user\/orders/);
    await expect(page.getByRole('heading', { name: 'Orders' })).toBeVisible();
  });
});
