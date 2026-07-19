namespace ShopifyOrderMonitorService.Shopify;

/// <summary>The GraphQL documents used to read orders and their line items.</summary>
static class GraphQlQueries
{
    // sortKey: CREATED_AT is load-bearing. The connection's default is PROCESSED_AT, and Shopify's
    // guidance is to sort by the field you filter on; a mismatch is slow or fails. Ascending order
    // also guarantees each page's newest createdAt only moves forward, so progress can be saved per page.
    public const string OrdersPage = """
        query NewOrdersPage($filter: String!, $first: Int!, $after: String, $lineItemsFirst: Int!, $shippingLinesFirst: Int!) {
          orders(first: $first, after: $after, query: $filter, sortKey: CREATED_AT, reverse: false) {
            pageInfo { hasNextPage endCursor }
            nodes {
              id
              legacyResourceId
              name
              confirmationNumber
              poNumber
              createdAt
              updatedAt
              processedAt
              cancelledAt
              cancelReason
              closedAt
              test
              currencyCode
              presentmentCurrencyCode
              displayFinancialStatus
              displayFulfillmentStatus
              returnStatus
              email
              phone
              note
              tags
              sourceName
              sourceIdentifier
              paymentGatewayNames
              totalPriceSet { ...MoneyFields }
              subtotalPriceSet { ...MoneyFields }
              totalTaxSet { ...MoneyFields }
              totalShippingPriceSet { ...MoneyFields }
              totalDiscountsSet { ...MoneyFields }
              customAttributes { key value }
              customer { id legacyResourceId firstName lastName email phone }
              billingAddress { ...AddressFields }
              shippingAddress { ...AddressFields }
              shippingLines(first: $shippingLinesFirst) {
                nodes {
                  title
                  code
                  source
                  originalPriceSet { ...MoneyFields }
                  discountedPriceSet { ...MoneyFields }
                }
              }
              lineItems(first: $lineItemsFirst) {
                pageInfo { hasNextPage endCursor }
                nodes { ...LineItemFields }
              }
            }
          }
        }

        fragment MoneyFields on MoneyBag { shopMoney { amount currencyCode } }

        fragment AddressFields on MailingAddress {
          firstName lastName company address1 address2 city province provinceCode country countryCodeV2 zip phone
        }

        fragment LineItemFields on LineItem {
          id name title variantTitle sku vendor quantity currentQuantity requiresShipping taxable isGiftCard
          originalUnitPriceSet { ...MoneyFields }
          discountedTotalSet { ...MoneyFields }
          customAttributes { key value }
          variant { id legacyResourceId sku title }
          product { id legacyResourceId handle }
        }
        """;

    public const string OrderLineItems = """
        query OrderLineItemsPage($id: ID!, $first: Int!, $after: String) {
          order(id: $id) {
            lineItems(first: $first, after: $after) {
              pageInfo { hasNextPage endCursor }
              nodes { ...LineItemFields }
            }
          }
        }

        fragment MoneyFields on MoneyBag { shopMoney { amount currencyCode } }

        fragment LineItemFields on LineItem {
          id name title variantTitle sku vendor quantity currentQuantity requiresShipping taxable isGiftCard
          originalUnitPriceSet { ...MoneyFields }
          discountedTotalSet { ...MoneyFields }
          customAttributes { key value }
          variant { id legacyResourceId sku title }
          product { id legacyResourceId handle }
        }
        """;
}
